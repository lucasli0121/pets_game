package api

import (
	"context"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"pets_server/internal/auth"
	"pets_server/internal/domain"
)

type ctxKey string

const (
	ctxUserID  ctxKey = "uid"
	ctxAdminID ctxKey = "aid"
	ctxRole    ctxKey = "role"
	ctxAdminNm ctxKey = "aname"
	ctxReqID   ctxKey = "rid"
)

func userID(ctx context.Context) string    { v, _ := ctx.Value(ctxUserID).(string); return v }
func adminID(ctx context.Context) string   { v, _ := ctx.Value(ctxAdminID).(string); return v }
func adminRole(ctx context.Context) string { v, _ := ctx.Value(ctxRole).(string); return v }
func adminName(ctx context.Context) string { v, _ := ctx.Value(ctxAdminNm).(string); return v }

// ---------- 指标（进程内，重启清零，用于看板异常指标） ----------

type Metrics struct {
	Total    atomic.Int64
	Errors   atomic.Int64
	Business atomic.Int64 // 业务错误（code != 0）
}

func (m *Metrics) ErrorRate() float64 {
	t := m.Total.Load()
	if t == 0 {
		return 0
	}
	return float64(m.Errors.Load()) / float64(t)
}

// mtr 包级指标。管理端看板的「接口错误率」读这里；进程重启清零，
// 只用于观察当下是否异常，不做历史留存（历史请查 action_log / event_log）。
var mtr = &Metrics{}

func (m *Metrics) BusinessErrorRate() float64 {
	t := m.Total.Load()
	if t == 0 {
		return 0
	}
	return float64(m.Business.Load()) / float64(t)
}

// ---------- 中间件 ----------

func recoverer() func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			defer func() {
				if rec := recover(); rec != nil {
					log.Printf("[PANIC] %s %s: %v", r.Method, r.URL.Path, rec)
					mtr.Errors.Add(1)
					writeJSON(w, http.StatusInternalServerError,
						Envelope{Code: domain.CodeInternal, Msg: "服务内部错误"})
				}
			}()
			next.ServeHTTP(w, r)
		})
	}
}

func cors(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type,Authorization")
		w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func countMetrics() func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			mtr.Total.Add(1)
			next.ServeHTTP(w, r)
		})
	}
}

// authClient 校验客户端 token。文档 6.6：云函数内校验 openid 与 user_id 的归属。
func (h *Handlers) authClient(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		tok := bearer(r)
		if tok == "" {
			fail(w, domain.Err(domain.CodeUnauthorized, "缺少登录凭证"))
			return
		}
		c, err := auth.Parse(h.cfg.JWTSecret, tok)
		if err != nil || c.Kind != "client" {
			fail(w, domain.Err(domain.CodeUnauthorized, "登录已失效，请重新登录"))
			return
		}
		ctx := context.WithValue(r.Context(), ctxUserID, c.Sub)
		// 频控（文档 6.6：同一接口 10 秒内超过 10 次拒绝）
		if !h.limiter.allow(c.Sub, r.URL.Path) {
			fail(w, domain.Err(domain.CodeRateLimited, "操作过于频繁，请稍后再试"))
			return
		}
		next(w, r.WithContext(ctx))
	}
}

// authAdmin 管理端鉴权 + 角色校验。
func (h *Handlers) authAdmin(allow ...string) func(http.HandlerFunc) http.HandlerFunc {
	return func(next http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			tok := bearer(r)
			if tok == "" {
				fail(w, domain.Err(domain.CodeUnauthorized, "请先登录"))
				return
			}
			c, err := auth.Parse(h.cfg.JWTSecret, tok)
			if err != nil || c.Kind != "admin" {
				fail(w, domain.Err(domain.CodeUnauthorized, "登录已失效，请重新登录"))
				return
			}
			if len(allow) > 0 && !roleAllowed(c.Role, allow) {
				fail(w, domain.Err(domain.CodeForbidden, "当前角色无此操作权限"))
				return
			}
			ctx := r.Context()
			ctx = context.WithValue(ctx, ctxAdminID, c.Sub)
			ctx = context.WithValue(ctx, ctxRole, c.Role)
			ctx = context.WithValue(ctx, ctxAdminNm, c.Name)
			next(w, r.WithContext(ctx))
		}
	}
}

// roleAllowed 超管放行一切；其余按白名单。
func roleAllowed(role string, allow []string) bool {
	if role == domain.RoleSuper {
		return true
	}
	for _, a := range allow {
		if a == role {
			return true
		}
	}
	return false
}

func bearer(r *http.Request) string {
	h := r.Header.Get("Authorization")
	if strings.HasPrefix(h, "Bearer ") {
		return strings.TrimSpace(h[7:])
	}
	// 兼容小游戏里不好设 header 的情况
	if v := r.Header.Get("X-Token"); v != "" {
		return v
	}
	return r.URL.Query().Get("token")
}

// ---------- 频控 ----------

type limiter struct {
	mu     sync.Mutex
	window time.Duration
	max    int
	hits   map[string][]time.Time
	lastGC time.Time
}

func newLimiter(window time.Duration, max int) *limiter {
	return &limiter{window: window, max: max, hits: map[string][]time.Time{}, lastGC: time.Now()}
}

func (l *limiter) allow(userKey, path string) bool {
	key := userKey + "|" + path
	now := time.Now()
	cut := now.Add(-l.window)

	l.mu.Lock()
	defer l.mu.Unlock()

	if now.Sub(l.lastGC) > 5*time.Minute {
		for k, ts := range l.hits {
			if len(ts) == 0 || ts[len(ts)-1].Before(cut) {
				delete(l.hits, k)
			}
		}
		l.lastGC = now
	}

	ts := l.hits[key]
	keep := ts[:0]
	for _, t := range ts {
		if t.After(cut) {
			keep = append(keep, t)
		}
	}
	if len(keep) >= l.max {
		l.hits[key] = keep
		return false
	}
	l.hits[key] = append(keep, now)
	return true
}

func clientIP(r *http.Request) string {
	if v := r.Header.Get("X-Real-IP"); v != "" {
		return v
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

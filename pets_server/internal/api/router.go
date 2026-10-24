package api

import (
	"net/http"
	"strings"
	"time"

	"pets_server/internal/config"
	"pets_server/internal/service"
	"pets_server/internal/store"
	"pets_server/internal/web"
)

type Handlers struct {
	cfg     *config.Config
	db      *store.DB
	cfgSvc  *service.ConfigService
	assets  *service.AssetsService
	idem    *service.IdemService
	pets    *service.PetService
	shop    *service.ShopService
	checkin *service.CheckInService
	ads     *service.AdService
	admin   *service.AdminService
	limiter *limiter
	routes  []RouteEntry
}

func New(cfg *config.Config, db *store.DB, cfgSvc *service.ConfigService,
	assets *service.AssetsService, idem *service.IdemService,
	pets *service.PetService, shop *service.ShopService,
	checkin *service.CheckInService, ads *service.AdService,
	admin *service.AdminService) *Handlers {
	return &Handlers{
		cfg: cfg, db: db, cfgSvc: cfgSvc, assets: assets, idem: idem,
		pets: pets, shop: shop, checkin: checkin, ads: ads, admin: admin,
		limiter: newLimiter(time.Duration(cfg.RateLimitWindowSec)*time.Second, cfg.RateLimitMaxCalls),
	}
}

// RouteEntry 一条已注册路由的元信息。
// 由 apiRoutes 在注册时填充，供 RouteTable() 导出（给一致性测试与接口文档用）。
type RouteEntry struct {
	Method string
	Path   string
}

// routeTable 累积所有注册过的 API 路由。不含 /admin/ 静态资源与 /healthz。
//
// 挂在 Handlers 上而不是包级变量：包级可变状态在并发下是数据竞争，
// 而每个请求处理链（app.Build → New → Routes）各自持有一个 Handlers 实例。
func (h *Handlers) routeTable() []RouteEntry {
	out := make([]RouteEntry, len(h.routes))
	copy(out, h.routes)
	return out
}

// RouteTable 返回已注册的 API 路由清单（客户端 + 管理端，不含静态资源）。
//
// 导出这个是为了让 test/apidoc_test.go 能把「路由」与「接口文档」逐条比对：
// 加了新接口却忘了写文档，测试会直接失败，而不是等客户端对接时才发现。
func (h *Handlers) RouteTable() []RouteEntry { return h.routeTable() }

// APISpecs 返回接口规格表副本。
func APISpecs() []APISpec {
	out := make([]APISpec, len(apiSpecs))
	copy(out, apiSpecs)
	return out
}

// handle 注册一条 API 路由并记录到 routeTable。
func (h *Handlers) handle(mux *http.ServeMux, pattern string, fn http.HandlerFunc) {
	sp := strings.IndexByte(pattern, ' ')
	h.routes = append(h.routes, RouteEntry{Method: pattern[:sp], Path: pattern[sp+1:]})
	mux.HandleFunc(pattern, fn)
}

// Routes 路由表。
//
// 客户端接口命名与文档第 5 节的 1-13 一一对应，另加两个文档未覆盖但必需的：
//
//	POST /api/v1/pet/interaction  互动经验（PRD 4.3 有数值，13 个接口里没有入口）
//	POST /api/v1/ad/impression    插屏曝光埋点（文档 13.8 ② 要求只记录不发奖）
func (h *Handlers) Routes() http.Handler {
	mux := http.NewServeMux()
	h.routes = h.routes[:0] // 允许重复调用 Routes()

	read := []string{"config", "readonly"}
	write := []string{"config"}

	// ---- 客户端 ----
	h.handle(mux, "POST /api/v1/login", h.login)
	h.handle(mux, "POST /api/v1/config/get", h.authClient(h.getConfig))
	h.handle(mux, "POST /api/v1/checkin/claim", h.authClient(h.claimCheckIn))
	h.handle(mux, "POST /api/v1/checkin/status", h.authClient(h.checkinStatus))
	h.handle(mux, "POST /api/v1/ad/reward", h.authClient(h.claimAdReward))
	h.handle(mux, "POST /api/v1/ad/impression", h.authClient(h.adImpression))
	h.handle(mux, "POST /api/v1/shop/buy-food", h.authClient(h.buyFood))
	h.handle(mux, "POST /api/v1/scene/unlock", h.authClient(h.unlockScene))
	h.handle(mux, "POST /api/v1/scene/use", h.authClient(h.useScene))
	h.handle(mux, "POST /api/v1/pet/feed", h.authClient(h.feedPet))
	h.handle(mux, "POST /api/v1/pet/switch", h.authClient(h.switchPet))
	h.handle(mux, "POST /api/v1/pet/adopt", h.authClient(h.adoptPet))
	h.handle(mux, "POST /api/v1/pet/personality", h.authClient(h.updatePersonality))
	h.handle(mux, "POST /api/v1/pet/interaction", h.authClient(h.reportInteraction))
	h.handle(mux, "POST /api/v1/sync-state", h.authClient(h.syncState))
	h.handle(mux, "POST /api/v1/event/report", h.authClient(h.reportEvent))

	// ---- 管理端 ----
	// A1 登录（公开）
	h.handle(mux, "POST /api/admin/login", h.adminLogin)

	// A1 / 自身信息
	h.handle(mux, "GET /api/admin/me", h.authAdmin(read...)(h.adminMe))

	// A2-A6 / A12 配置读写（草稿态）
	h.handle(mux, "GET /api/admin/config/effective", h.authAdmin(read...)(h.cfgEffective))
	h.handle(mux, "GET /api/admin/config/draft", h.authAdmin(read...)(h.cfgDraft))
	h.handle(mux, "PUT /api/admin/config/draft", h.authAdmin(write...)(h.cfgSaveDraft))
	h.handle(mux, "DELETE /api/admin/config/draft", h.authAdmin(write...)(h.cfgDiscardDraft))
	h.handle(mux, "GET /api/admin/config/diff", h.authAdmin(read...)(h.cfgDiff))
	h.handle(mux, "POST /api/admin/config/publish", h.authAdmin(write...)(h.cfgPublish))
	h.handle(mux, "GET /api/admin/config/releases", h.authAdmin(read...)(h.cfgReleases))
	h.handle(mux, "POST /api/admin/config/rollback", h.authAdmin(write...)(h.cfgRollback))

	// A8 用户查询
	h.handle(mux, "GET /api/admin/users", h.authAdmin(read...)(h.adminUsers))
	h.handle(mux, "GET /api/admin/users/{id}", h.authAdmin(read...)(h.adminUserDetail))

	// A9 积分调整（仅超管）
	h.handle(mux, "POST /api/admin/points/adjust", h.authAdmin()(h.adminAdjustPoints))

	// A10 / A11
	h.handle(mux, "GET /api/admin/dashboard", h.authAdmin(read...)(h.adminDashboard))
	h.handle(mux, "GET /api/admin/audit-logs", h.authAdmin(read...)(h.adminAuditLogs))

	// 接口文档（给客户端对接与运营查手册用，不额外收紧权限）
	h.handle(mux, "GET /api/admin/api-doc", h.authAdmin(read...)(h.apiDocHandler))

	// ---- 静态资源（不计入 routeTable）----
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		ok(w, map[string]any{"status": "ok", "service": h.cfg.AppName})
	})
	// 场景背景图 / 缩略图。本地联调与单机部署用；
	// 生产走 CDN（把 ASSET_BASE_URL 指过去）时这条路由不会被访问。
	mux.Handle("GET /assets/", http.StripPrefix("/assets/", assetHandler(h.cfg.AssetDir)))
	mux.Handle("GET /admin/", http.StripPrefix("/admin/", web.AdminHandler(h.cfg.AdminDir)))
	mux.HandleFunc("GET /admin", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/admin/", http.StatusFound)
	})
	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/" {
			http.Redirect(w, r, "/admin/", http.StatusFound)
			return
		}
		http.NotFound(w, r)
	})

	// 中间件：recover → metrics → cors
	return recoverer()(countMetrics()(cors(mux)))
}

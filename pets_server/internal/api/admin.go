package api

import (
	"net/http"
	"strconv"
	"strings"

	"pets_server/internal/domain"
	"pets_server/internal/service"
)

// ---------- A1 登录 ----------

type adminLoginReq struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

func (h *Handlers) adminLogin(w http.ResponseWriter, r *http.Request) {
	var req adminLoginReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	if strings.TrimSpace(req.Username) == "" || req.Password == "" {
		fail(w, domain.ErrParam("请输入账号和密码"))
		return
	}
	res, err := h.admin.Login(r.Context(), req.Username, req.Password, clientIP(r))
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, res)
}

func (h *Handlers) adminMe(w http.ResponseWriter, r *http.Request) {
	a, err := h.admin.AdminByID(r.Context(), adminID(r.Context()))
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"admin": a, "role": a.Role})
}

// ---------- A2-A6 / A12 配置：读生效态 ----------

func (h *Handlers) cfgEffective(w http.ResponseWriter, r *http.Request) {
	snap, ver, err := h.cfgSvc.Effective(r.Context())
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"version": ver, "config": snap})
}

func (h *Handlers) cfgDraft(w http.ResponseWriter, r *http.Request) {
	snap, by, at, err := h.cfgSvc.Draft(r.Context())
	if err != nil {
		fail(w, err)
		return
	}
	_, ver, _ := h.cfgSvc.Effective(r.Context())
	ok(w, map[string]any{
		"config": snap, "version": ver,
		"updatedBy": by, "updatedAt": at,
		"hasDraft": h.cfgSvc.HasDraft(r.Context()),
	})
}

type saveDraftReq struct {
	Config domain.ConfigSnapshot `json:"config"`
}

func (h *Handlers) cfgSaveDraft(w http.ResponseWriter, r *http.Request) {
	var req saveDraftReq
	if err := decodeLoose(r, &req); err != nil {
		fail(w, err)
		return
	}
	if err := service.ValidateSnapshot(&req.Config); err != nil {
		fail(w, err)
		return
	}

	// 文档 7.4：配置运营不可修改积分规则与广告位
	if adminRole(r.Context()) == domain.RoleConfig {
		prev, _, _, err := h.cfgSvc.Draft(r.Context())
		if err != nil {
			fail(w, err)
			return
		}
		if !equalJSON(prev.Points, req.Config.Points) || !equalJSON(prev.Ad, req.Config.Ad) {
			fail(w, domain.ErrForbidden("配置运营角色不可修改积分数值与广告位"))
			return
		}
	}

	if err := h.cfgSvc.SaveDraft(r.Context(), &req.Config, adminName(r.Context())); err != nil {
		fail(w, err)
		return
	}
	_ = h.admin.WriteAudit(r.Context(), adminID(r.Context()), adminName(r.Context()),
		"config_draft_save", "config", "", "", "保存草稿", clientIP(r))
	ok(w, map[string]any{"saved": true})
}

func (h *Handlers) cfgDiscardDraft(w http.ResponseWriter, r *http.Request) {
	if err := h.cfgSvc.DiscardDraft(r.Context()); err != nil {
		fail(w, err)
		return
	}
	_ = h.admin.WriteAudit(r.Context(), adminID(r.Context()), adminName(r.Context()),
		"config_draft_discard", "config", "", "", "", clientIP(r))
	ok(w, map[string]any{"discarded": true})
}

// ---------- A7 发布 / diff / 回滚 ----------

func (h *Handlers) cfgDiff(w http.ResponseWriter, r *http.Request) {
	changes, err := h.cfgSvc.Diff(r.Context())
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"changes": changes, "count": len(changes)})
}

type publishReq struct {
	Note string `json:"note"`
}

func (h *Handlers) cfgPublish(w http.ResponseWriter, r *http.Request) {
	var req publishReq
	_ = decodeLoose(r, &req)
	ver, err := h.cfgSvc.Publish(r.Context(), req.Note, adminName(r.Context()))
	if err != nil {
		fail(w, err)
		return
	}
	_ = h.admin.WriteAudit(r.Context(), adminID(r.Context()), adminName(r.Context()),
		"config_publish", "config", "", "v"+strconv.FormatInt(ver, 10), req.Note, clientIP(r))
	ok(w, map[string]any{
		"version": ver,
		"hint":    "已发布。客户端下次启动或比对版本号时才会拉取新配置，不是立刻全量生效。",
	})
}

func (h *Handlers) cfgReleases(w http.ResponseWriter, r *http.Request) {
	limit, _ := strconv.ParseInt(q(r, "limit", "20"), 10, 64)
	list, err := h.cfgSvc.Releases(r.Context(), limit)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"releases": list})
}

type rollbackReq struct {
	Version int64  `json:"version"`
	Note    string `json:"note"`
}

func (h *Handlers) cfgRollback(w http.ResponseWriter, r *http.Request) {
	var req rollbackReq
	if err := decodeLoose(r, &req); err != nil {
		fail(w, err)
		return
	}
	if req.Version <= 0 {
		fail(w, domain.ErrParam("请指定要回滚到的版本号"))
		return
	}
	ver, err := h.cfgSvc.Rollback(r.Context(), req.Version, req.Note, adminName(r.Context()))
	if err != nil {
		fail(w, err)
		return
	}
	_ = h.admin.WriteAudit(r.Context(), adminID(r.Context()), adminName(r.Context()),
		"config_rollback", "config",
		"v"+strconv.FormatInt(req.Version, 10), "v"+strconv.FormatInt(ver, 10), req.Note, clientIP(r))
	ok(w, map[string]any{"version": ver, "hint": "回滚已生效，客户端下次比对版本号时拉取。"})
}

// ---------- A12 等级表生成 ----------

// ---------- A8 用户查询 ----------

func (h *Handlers) adminUsers(w http.ResponseWriter, r *http.Request) {
	page, _ := strconv.ParseInt(q(r, "page", "1"), 10, 64)
	size, _ := strconv.ParseInt(q(r, "size", "20"), 10, 64)
	list, total, err := h.admin.SearchUsers(r.Context(), q(r, "keyword", ""), page, size)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"list": list, "total": total, "page": page, "size": size})
}

func (h *Handlers) adminUserDetail(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	d, err := h.admin.UserDetail(r.Context(), id)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, d)
}

// ---------- A9 积分手动调整 ----------

type adjustReq struct {
	UserID string `json:"userId"`
	Delta  int    `json:"delta"`
	Note   string `json:"note"`
}

func (h *Handlers) adminAdjustPoints(w http.ResponseWriter, r *http.Request) {
	var req adjustReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	res, err := h.admin.AdjustPoints(r.Context(), adminID(r.Context()), adminName(r.Context()),
		req.UserID, req.Delta, req.Note, clientIP(r))
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, res)
}

// ---------- A10 看板 ----------

func (h *Handlers) adminDashboard(w http.ResponseWriter, r *http.Request) {
	days, _ := strconv.Atoi(q(r, "days", "7"))
	d, err := h.admin.Dashboard(r.Context(), days)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{
		"dashboard":             d,
		"httpErrorRate":         round4(mtr.ErrorRate()),
		"httpBusinessErrorRate": round4(mtr.BusinessErrorRate()),
		"httpRequestsSinceBoot": mtr.Total.Load(),
	})
}

// ---------- A11 操作日志 ----------

func (h *Handlers) adminAuditLogs(w http.ResponseWriter, r *http.Request) {
	limit, _ := strconv.ParseInt(q(r, "limit", "100"), 10, 64)
	list, err := h.admin.AuditLogs(r.Context(), limit)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"list": list})
}

func round4(v float64) float64 { return float64(int(v*10000+0.5)) / 10000 }

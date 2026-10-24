package api

import (
	"context"
	"net/http"
	"time"

	"pets_server/internal/auth"
	"pets_server/internal/domain"
	"pets_server/internal/service"
	"pets_server/internal/timeutil"
)

// ---------- 1. login ----------

type loginReq struct {
	Code      string `json:"code"`      // wx.login 拿到的 code
	DevOpenID string `json:"devOpenid"` // 仅本地联调：DEBUG 且未配 WxAppID 时可用
	Nickname  string `json:"nickname"`
	Avatar    string `json:"avatar"`
}

type loginResp struct {
	Token string `json:"token"`
	*service.LoginResult
}

func (h *Handlers) login(w http.ResponseWriter, r *http.Request) {
	var req loginReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}

	openid, unionid := "", ""
	switch {
	case h.cfg.WxAppID != "" && h.cfg.WxSecret != "" && req.Code != "":
		id, err := wxCode2Session(r.Context(), h.cfg.WxAppID, h.cfg.WxSecret, req.Code)
		if err != nil {
			fail(w, domain.Err(domain.CodeUnauthorized, err.Error()))
			return
		}
		openid, unionid = id.OpenID, id.UnionID
	case h.cfg.AllowDevLogin && h.cfg.Debug && req.DevOpenID != "":
		// 本地/内测用：直接把 devOpenid 当 openid。生产必须关闭 ALLOW_DEV_LOGIN。
		openid = "dev_" + req.DevOpenID
	default:
		fail(w, domain.Err(domain.CodeUnauthorized, "登录失败：缺少 code"))
		return
	}

	res, err := h.pets.Login(r.Context(), openid, unionid, req.Nickname, req.Avatar)
	if err != nil {
		fail(w, err)
		return
	}
	exp := time.Now().Add(time.Duration(h.cfg.ClientTokenTTLH) * time.Hour).Unix()
	tok, err := auth.Sign(h.cfg.JWTSecret, auth.Claims{
		Sub: res.User.ID, Kind: "client", Exp: exp,
	})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, loginResp{Token: tok, LoginResult: res})
}

// ---------- 2. getConfig ----------

type getConfigReq struct {
	Version int64 `json:"version"`
}

// getConfig 版本一致只回 unchanged，不一致回全量（文档 6.5）。
func (h *Handlers) getConfig(w http.ResponseWriter, r *http.Request) {
	var req getConfigReq
	_ = decode(r, &req) // 允许空体

	snap, ver, err := h.cfgSvc.ClientSnapshot(r.Context())
	if err != nil {
		fail(w, err)
		return
	}
	if req.Version > 0 && req.Version == ver {
		ok(w, map[string]any{"unchanged": true, "version": ver})
		return
	}
	ok(w, map[string]any{"unchanged": false, "version": ver, "config": snap})
}

// ---------- 3. claimCheckIn ----------

type requestIDReq struct {
	RequestID string `json:"requestId"`
}

func (h *Handlers) claimCheckIn(w http.ResponseWriter, r *http.Request) {
	var req requestIDReq
	_ = decode(r, &req)
	uid := userID(r.Context())
	if err := requireRequestID(req.RequestID); err != nil {
		fail(w, err)
		return
	}
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "claimCheckIn",
		func(ctx context.Context) (any, error) { return h.checkin.Claim(ctx, uid) })
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 3b. checkinStatus ----------

// checkinStatus 只读查询签到状态。**不发奖、不写库**。
//
// 为什么要有这个独立接口：checkin/claim 是领奖动作，调一次就发积分，
// 客户端不能拿它当「今天签没签」用。sync-state 虽然也带 checkin 字段，
// 但它是全量同步（宠物 + 积分 + 背包 + 场景），为了看一眼签到状态发一次太重，
// 而且会顺带触发离线衰减结算。
//
// 无请求参数，空 body 即可。
func (h *Handlers) checkinStatus(w http.ResponseWriter, r *http.Request) {
	data, err := h.checkin.StatusDetail(r.Context(), userID(r.Context()))
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 4. claimAdReward ----------

type adRewardReq struct {
	RequestID       string `json:"requestId"`
	AdUnit          string `json:"adUnit"`
	Placement       string `json:"placement"`
	ClientCompleted bool   `json:"clientCompleted"`
}

func (h *Handlers) claimAdReward(w http.ResponseWriter, r *http.Request) {
	var req adRewardReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	if err := requireRequestID(req.RequestID); err != nil {
		fail(w, err)
		return
	}
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "claimAdReward",
		func(ctx context.Context) (any, error) {
			return h.ads.ClaimReward(ctx, uid, req.AdUnit, req.Placement, req.ClientCompleted)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

type adImpressionReq struct {
	RequestID string `json:"requestId"`
	AdUnit    string `json:"adUnit"`
	Placement string `json:"placement"`
	Completed bool   `json:"completed"`
}

func (h *Handlers) adImpression(w http.ResponseWriter, r *http.Request) {
	var req adImpressionReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "adImpression",
		func(ctx context.Context) (any, error) {
			return h.ads.Impression(ctx, uid, req.AdUnit, req.Placement, req.Completed)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 5. buyFood ----------

type buyFoodReq struct {
	RequestID string `json:"requestId"`
	FoodID    string `json:"foodId"`
	Count     int    `json:"count"`
}

func (h *Handlers) buyFood(w http.ResponseWriter, r *http.Request) {
	var req buyFoodReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	if err := requireRequestID(req.RequestID); err != nil {
		fail(w, err)
		return
	}
	if req.Count <= 0 {
		req.Count = 1
	}
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "buyFood",
		func(ctx context.Context) (any, error) {
			return h.shop.BuyFood(ctx, uid, req.FoodID, req.Count, req.RequestID)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 6/7. scene ----------

type unlockSceneReq struct {
	RequestID string `json:"requestId"`
	SceneID   string `json:"sceneId"`
}

func (h *Handlers) unlockScene(w http.ResponseWriter, r *http.Request) {
	var req unlockSceneReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	if err := requireRequestID(req.RequestID); err != nil {
		fail(w, err)
		return
	}
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "unlockScene",
		func(ctx context.Context) (any, error) {
			return h.shop.UnlockScene(ctx, uid, req.SceneID, req.RequestID)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

type useSceneReq struct {
	SceneID string `json:"sceneId"`
}

func (h *Handlers) useScene(w http.ResponseWriter, r *http.Request) {
	var req useSceneReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	scene, err := h.shop.UseScene(r.Context(), userID(r.Context()), req.SceneID)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"activeSceneId": scene})
}

// ---------- 8. feedPet ----------

type feedPetReq struct {
	RequestID string `json:"requestId"`
	PetID     string `json:"petId"`
	FoodID    string `json:"foodId"`
}

func (h *Handlers) feedPet(w http.ResponseWriter, r *http.Request) {
	var req feedPetReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	if err := requireRequestID(req.RequestID); err != nil {
		fail(w, err)
		return
	}
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "feedPet",
		func(ctx context.Context) (any, error) {
			return h.pets.Feed(ctx, uid, req.PetID, req.FoodID)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 9. switchPet ----------

type switchPetReq struct {
	PetID string `json:"petId"`
}

func (h *Handlers) switchPet(w http.ResponseWriter, r *http.Request) {
	var req switchPetReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	pets, active, err := h.pets.Switch(r.Context(), userID(r.Context()), req.PetID)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"activePetId": active, "pets": pets})
}

// ---------- 10. adoptPet ----------

type adoptPetReq struct {
	PetType     string             `json:"petType"`
	Name        string             `json:"name"`
	Personality domain.Personality `json:"personality"`
}

func (h *Handlers) adoptPet(w http.ResponseWriter, r *http.Request) {
	var req adoptPetReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	pet, err := h.pets.Adopt(r.Context(), userID(r.Context()), service.AdoptReq{
		PetType: req.PetType, Name: req.Name, Personality: req.Personality,
	})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, map[string]any{"pet": pet})
}

// ---------- 11. updatePersonality ----------

type personalityReq struct {
	RequestID   string             `json:"requestId"`
	PetID       string             `json:"petId"`
	Personality domain.Personality `json:"personality"`
}

func (h *Handlers) updatePersonality(w http.ResponseWriter, r *http.Request) {
	var req personalityReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	if err := requireRequestID(req.RequestID); err != nil {
		fail(w, err)
		return
	}
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "updatePersonality",
		func(ctx context.Context) (any, error) {
			return h.pets.UpdatePersonality(ctx, uid, req.PetID, req.Personality)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 12. syncState ----------

type syncStateReq struct {
	PetID       string `json:"petId"`
	ClientState any    `json:"clientState"`
	ClientTs    int64  `json:"clientTs"`
}

// syncState 只回服务端权威状态，client_state 一律不回写（文档 6.6）。
func (h *Handlers) syncState(w http.ResponseWriter, r *http.Request) {
	var req syncStateReq
	_ = decode(r, &req)
	data, err := h.pets.SyncState(r.Context(), userID(r.Context()), req.PetID)
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

// ---------- 13. reportEvent ----------

type reportEventReq struct {
	Event   string         `json:"event"`
	Payload map[string]any `json:"payload"`
}

func (h *Handlers) reportEvent(w http.ResponseWriter, r *http.Request) {
	var req reportEventReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	if req.Event == "" {
		fail(w, domain.ErrParam("event 不能为空"))
		return
	}
	uid := userID(r.Context())
	h.db.C("event_log").InsertOne(r.Context(), map[string]any{
		"user_id": uid, "event": req.Event, "payload": req.Payload,
		"created_at": timeutil.NowMs(),
	})
	ok(w, nil)
}

// ---------- 补充：互动上报 ----------

type interactionReq struct {
	RequestID string `json:"requestId"`
	PetID     string `json:"petId"`
	Gesture   string `json:"gesture"`
	Count     int    `json:"count"`
}

// reportInteraction 见 service.PetService.Interact 注释：文档 13 个接口未覆盖互动经验，
// 但 PRD 4.3 要求互动涨快乐/经验，且服务端必须是唯一的数值来源。
func (h *Handlers) reportInteraction(w http.ResponseWriter, r *http.Request) {
	var req interactionReq
	if err := decode(r, &req); err != nil {
		fail(w, err)
		return
	}
	uid := userID(r.Context())
	data, _, err := h.idem.Run(r.Context(), uid, req.RequestID, "reportInteraction",
		func(ctx context.Context) (any, error) {
			return h.pets.Interact(ctx, uid, req.PetID, req.Gesture, req.Count)
		})
	if err != nil {
		fail(w, err)
		return
	}
	ok(w, data)
}

func requireRequestID(id string) error {
	if id == "" {
		return domain.ErrParam("缺少 requestId")
	}
	if len(id) > 64 {
		return domain.ErrParam("requestId 过长")
	}
	return nil
}

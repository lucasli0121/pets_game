package service

import (
	"context"
	"strconv"
	"time"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/auth"
	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

type AdminService struct {
	db     *store.DB
	cfg    *ConfigService
	assets *AssetsService
	secret string
	tokenH int
}

func NewAdminService(db *store.DB, cfg *ConfigService, assets *AssetsService, secret string, tokenHours int) *AdminService {
	return &AdminService{db: db, cfg: cfg, assets: assets, secret: secret, tokenH: tokenHours}
}

type AdminLoginResult struct {
	Token string           `json:"token"`
	Admin domain.AdminUser `json:"admin"`
	Exp   int64            `json:"exp"`
}

func (s *AdminService) Login(ctx context.Context, username, password, ip string) (*AdminLoginResult, error) {
	var a domain.AdminUser
	err := s.db.C(store.CollAdminUsers).FindOne(ctx, store.F("username", username)).Decode(&a)
	if err == mongo.ErrNoDocuments {
		return nil, domain.Err(domain.CodeUnauthorized, "账号或密码错误")
	}
	if err != nil {
		return nil, err
	}
	if a.Status != "active" {
		return nil, domain.ErrForbidden("账号已停用")
	}
	if !auth.CheckPassword(a.PasswordHash, password) {
		return nil, domain.Err(domain.CodeUnauthorized, "账号或密码错误")
	}
	exp := time.Now().Add(time.Duration(s.tokenH) * time.Hour).Unix()
	token, err := auth.Sign(s.secret, auth.Claims{
		Sub: a.ID, Kind: "admin", Role: a.Role, Name: a.Username, Exp: exp,
	})
	if err != nil {
		return nil, err
	}
	_, _ = s.db.C(store.CollAdminUsers).UpdateOne(ctx, store.F("_id", a.ID),
		bson.D{{Key: "$set", Value: bson.D{{Key: "last_login_at", Value: timeutil.NowMs()}}}})
	_ = s.WriteAudit(ctx, a.ID, a.Username, "login", "admin", "", "", "", ip)
	return &AdminLoginResult{Token: token, Admin: a, Exp: exp}, nil
}

func (s *AdminService) AdminByID(ctx context.Context, id string) (*domain.AdminUser, error) {
	var a domain.AdminUser
	if err := s.db.C(store.CollAdminUsers).FindOne(ctx, store.F("_id", id)).Decode(&a); err != nil {
		return nil, err
	}
	return &a, nil
}

// WriteAudit 管理端所有写操作都要留痕（文档 6.6）。
func (s *AdminService) WriteAudit(ctx context.Context, adminID, adminName, action, target, before, after, note, ip string) error {
	_, err := s.db.C(store.CollAuditLog).InsertOne(ctx, domain.AuditLog{
		ID: newID(), AdminID: adminID, AdminName: adminName, Action: action,
		Target: target, Before: before, After: after, Note: note, IP: ip,
		CreatedAt: timeutil.NowMs(),
	})
	return err
}

func (s *AdminService) AuditLogs(ctx context.Context, limit int64) ([]domain.AuditLog, error) {
	if limit <= 0 || limit > 500 {
		limit = 100
	}
	cur, err := s.db.C(store.CollAuditLog).Find(ctx, bson.M{},
		options.Find().SetSort(store.D("created_at", -1)).SetLimit(limit))
	if err != nil {
		return nil, err
	}
	var out []domain.AuditLog
	return out, cur.All(ctx, &out)
}

// ---------- 用户查询 ----------

type UserBrief struct {
	domain.User
	Balance      int   `json:"balance"`
	TotalEarned  int   `json:"totalEarned"`
	TotalSpent   int   `json:"totalSpent"`
	PetCount     int   `json:"petCount"`
	LastActiveAt int64 `json:"lastActiveAt"`

	// A8 列表页也要能一眼看到活跃度，所以这几个字段直接带在列表里，
	// 不必点进详情。
	ActiveDays   int     `json:"activeDays"`
	LoginCount   int     `json:"loginCount"`
	LoginPerDay  float64 `json:"loginPerDay"`
	MaxOnlineMin float64 `json:"maxOnlineMinutes"`
	FeedPerDay   float64 `json:"feedPerDay"`
	SpentPerDay  float64 `json:"pointsSpentPerDay"`
}

func (s *AdminService) SearchUsers(ctx context.Context, keyword string, page, size int64) ([]UserBrief, int64, error) {
	if page < 1 {
		page = 1
	}
	if size <= 0 || size > 100 {
		size = 20
	}
	filter := bson.M{}
	if keyword != "" {
		filter["$or"] = bson.A{
			bson.M{"openid": keyword},
			bson.M{"_id": keyword},
			bson.M{"nickname": bson.M{"$regex": keyword, "$options": "i"}},
		}
	}
	total, err := s.db.C(store.CollUsers).CountDocuments(ctx, filter)
	if err != nil {
		return nil, 0, err
	}
	cur, err := s.db.C(store.CollUsers).Find(ctx, filter,
		options.Find().SetSort(store.D("created_at", -1)).
			SetSkip((page-1)*size).SetLimit(size))
	if err != nil {
		return nil, 0, err
	}
	var users []domain.User
	if err := cur.All(ctx, &users); err != nil {
		return nil, 0, err
	}

	out := make([]UserBrief, 0, len(users))
	ids := make([]string, 0, len(users))
	for _, u := range users {
		ids = append(ids, u.ID)
	}
	// 列表要展示活跃度指标，逐个用户查会变成 N+1（每用户 6+ 次 count）。
	// 这里对整页一次性聚合，随页大小线性增长的查询数恒为 3 次。
	stats := aggregateBriefStats(ctx, s.db, ids)

	for _, u := range users {
		b := UserBrief{User: u}
		var as domain.Assets
		if err := s.db.C(store.CollAssets).FindOne(ctx, store.F("_id", u.ID)).Decode(&as); err == nil {
			b.Balance, b.TotalEarned, b.TotalSpent = as.Points.Balance, as.Points.TotalEarned, as.Points.TotalSpent
		}
		n, _ := s.db.C(store.CollPets).CountDocuments(ctx, store.F("user_id", u.ID))
		b.PetCount = int(n)

		if st, ok := stats[u.ID]; ok {
			b.ActiveDays = st.days
			b.LoginCount = st.logins
			b.LoginPerDay = perDay(st.logins, st.days)
			b.MaxOnlineMin = round1(float64(st.maxOnline) / 60000)
			b.FeedPerDay = perDay(st.feeds, st.days)
			b.SpentPerDay = perDay(st.spent, st.days)
		}
		out = append(out, b)
	}
	return out, total, nil
}

func perDay(total, days int) float64 {
	if days <= 0 {
		return 0
	}
	return round2(float64(total) / float64(days))
}

// briefStats 单个用户的列表级统计（详情页的完整口径见 StatsOf）。
type briefStats struct {
	days      int
	logins    int
	feeds     int
	spent     int
	maxOnline int64
}

// aggregateBriefStats 对一页用户做 3 次聚合（活跃天数 / 登录数 / 喂养数+花费），
// 而不是每用户查一遍。
func aggregateBriefStats(ctx context.Context, db *store.DB, ids []string) map[string]briefStats {
	out := map[string]briefStats{}
	if len(ids) == 0 {
		return out
	}
	in := bson.A{}
	for _, id := range ids {
		in = append(in, id)
	}

	get := func(coll string, pipeline []bson.D) []bson.Raw {
		cur, err := db.C(coll).Aggregate(ctx, pipeline)
		if err != nil {
			return nil
		}
		var rows []bson.Raw
		if cur.All(ctx, &rows) != nil {
			return nil
		}
		return rows
	}

	for _, raw := range get(store.CollDailyActive, []bson.D{
		{{Key: "$match", Value: bson.M{"user_id": bson.M{"$in": in}}}},
		{{Key: "$group", Value: bson.M{"_id": "$user_id", "days": bson.M{"$sum": 1}}}},
	}) {
		var r struct {
			ID   string `bson:"_id"`
			Days int    `bson:"days"`
		}
		if bson.Unmarshal(raw, &r) == nil {
			st := out[r.ID]
			st.days = r.Days
			out[r.ID] = st
		}
	}

	for _, raw := range get(store.CollActionLog, []bson.D{
		{{Key: "$match", Value: bson.M{"user_id": bson.M{"$in": in}, "action": ActionFeed}}},
		{{Key: "$group", Value: bson.M{"_id": "$user_id", "feeds": bson.M{"$sum": 1}}}},
	}) {
		var r struct {
			ID    string `bson:"_id"`
			Feeds int    `bson:"feeds"`
		}
		if bson.Unmarshal(raw, &r) == nil {
			st := out[r.ID]
			st.feeds = r.Feeds
			out[r.ID] = st
		}
	}

	for _, raw := range get(store.CollLedger, []bson.D{
		{{Key: "$match", Value: bson.M{"user_id": bson.M{"$in": in}, "delta": bson.M{"$lt": 0}}}},
		{{Key: "$group", Value: bson.M{"_id": "$user_id", "spent": bson.M{"$sum": bson.M{"$multiply": bson.A{-1, "$delta"}}}}}},
	}) {
		var r struct {
			ID    string `bson:"_id"`
			Spent int    `bson:"spent"`
		}
		if bson.Unmarshal(raw, &r) == nil {
			st := out[r.ID]
			st.spent = r.Spent
			out[r.ID] = st
		}
	}

	// 登录次数与最大在线时长在 users 上，直接读，不必聚合。
	cur, err := db.C(store.CollUsers).Find(ctx, bson.M{"_id": bson.M{"$in": in}},
		options.Find().SetProjection(bson.M{"login_count": 1, "max_online_ms": 1}))
	if err == nil {
		var rows []struct {
			ID          string `bson:"_id"`
			LoginCount  int    `bson:"login_count"`
			MaxOnlineMs int64  `bson:"max_online_ms"`
		}
		if cur.All(ctx, &rows) == nil {
			for _, r := range rows {
				st := out[r.ID]
				st.logins = r.LoginCount
				st.maxOnline = r.MaxOnlineMs
				out[r.ID] = st
			}
		}
	}
	return out
}

type UserDetail struct {
	User     domain.User      `json:"user"`
	Assets   *domain.Assets   `json:"assets"`
	Pets     []domain.PetView `json:"pets"`
	Ledger   []domain.Ledger  `json:"ledger"`
	Orders   []domain.Order   `json:"orders"`
	CheckIns []domain.CheckIn `json:"checkIns"`
	AdEvents []domain.AdEvent `json:"adEvents"`

	// 行为统计与喂养记录（A8 用户资料）
	Stats       *UserStats   `json:"stats"`
	FeedRecords []FeedRecord `json:"feedRecords"`
	StatsBasis  string       `json:"statsBasis"`  // 日均口径说明，直接显示在页面上，避免误读
	StatsCaveat string       `json:"statsCaveat"` // 已知偏差说明
}

func (s *AdminService) UserDetail(ctx context.Context, userID string) (*UserDetail, error) {
	var u domain.User
	err := s.db.C(store.CollUsers).FindOne(ctx, bson.M{"$or": bson.A{
		bson.M{"_id": userID}, bson.M{"openid": userID},
	}}).Decode(&u)
	if err == mongo.ErrNoDocuments {
		return nil, domain.ErrNotFound("用户不存在")
	}
	if err != nil {
		return nil, err
	}

	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	as, err := s.assets.Get(ctx, u.ID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}

	d := &UserDetail{User: u, Assets: as}

	cur, err := s.db.C(store.CollPets).Find(ctx, store.F("user_id", u.ID))
	if err != nil {
		return nil, err
	}
	var pets []domain.Pet
	if err := cur.All(ctx, &pets); err != nil {
		return nil, err
	}
	curve := domain.NewLevelCurve(snap.Level)
	for _, p := range pets {
		d.Pets = append(d.Pets, buildPetView(p, curve, snap.Points))
	}

	d.Ledger, _ = s.assets.Ledger(ctx, u.ID, 50)
	d.Orders, _ = s.orders(ctx, u.ID, 30)
	d.CheckIns, _ = s.checkins(ctx, u.ID, 30)
	d.AdEvents, _ = s.adEvents(ctx, u.ID, 30)

	// 行为统计与喂养记录（A8）。
	// 老用户是在这套统计上线之前注册的，users 上没有 login_count / active_days，
	// 所以优先从 daily_active 与 action_log 现算，能补多少补多少。
	d.Stats = StatsOf(ctx, s.db, u.ID)
	d.StatsBasis = "日均 = 累计值 ÷ 活跃天数（有过登录的 UTC+8 自然日数），不是「注册至今天数」——后者会把未活跃的日子算进分母，新用户首日均分被拉低。"
	d.StatsCaveat = "在线时长按「相邻两次登录的时间差」估算，单次封顶 2 小时；喂养记录的花费积分按食物当前单价估算，实际扣分发生在购买食物时。"
	prices, names := FoodMeta(ctx, s.db)
	d.FeedRecords, _ = FeedRecords(ctx, s.db, u.ID, 50, prices, names, PetNameMap(ctx, s.db, u.ID))
	return d, nil
}

func (s *AdminService) orders(ctx context.Context, userID string, limit int64) ([]domain.Order, error) {
	cur, err := s.db.C(store.CollOrders).Find(ctx, store.F("user_id", userID),
		options.Find().SetSort(store.D("created_at", -1)).SetLimit(limit))
	if err != nil {
		return nil, err
	}
	var out []domain.Order
	return out, cur.All(ctx, &out)
}

func (s *AdminService) checkins(ctx context.Context, userID string, limit int64) ([]domain.CheckIn, error) {
	cur, err := s.db.C(store.CollCheckIn).Find(ctx, store.F("user_id", userID),
		options.Find().SetSort(store.D("created_at", -1)).SetLimit(limit))
	if err != nil {
		return nil, err
	}
	var out []domain.CheckIn
	return out, cur.All(ctx, &out)
}

func (s *AdminService) adEvents(ctx context.Context, userID string, limit int64) ([]domain.AdEvent, error) {
	cur, err := s.db.C(store.CollAdEvents).Find(ctx, store.F("user_id", userID),
		options.Find().SetSort(store.D("created_at", -1)).SetLimit(limit))
	if err != nil {
		return nil, err
	}
	var out []domain.AdEvent
	return out, cur.All(ctx, &out)
}

type AdjustResult struct {
	Balance  int    `json:"balance"`
	Delta    int    `json:"delta"`
	LedgerID string `json:"ledgerId"`
}

// AdjustPoints 手动加/扣积分，必须填原因（文档 A9）。
func (s *AdminService) AdjustPoints(ctx context.Context, operatorID, operatorName, userID string,
	delta int, note, ip string) (*AdjustResult, error) {
	if delta == 0 {
		return nil, domain.ErrParam("调整值不能为 0")
	}
	if note == "" {
		return nil, domain.ErrParam("必须填写调整原因")
	}
	if abs(delta) > 1_000_000 {
		return nil, domain.ErrParam("单次调整绝对值不能超过 1,000,000")
	}

	var u domain.User
	err := s.db.C(store.CollUsers).FindOne(ctx, bson.M{"$or": bson.A{
		bson.M{"_id": userID}, bson.M{"openid": userID},
	}}).Decode(&u)
	if err == mongo.ErrNoDocuments {
		return nil, domain.ErrNotFound("用户不存在")
	}
	if err != nil {
		return nil, err
	}

	var before int
	if as, e := s.assets.Get(ctx, u.ID, ""); e == nil {
		before = as.Points.Balance
	}

	var bal int
	var ledgerID string
	if delta > 0 {
		bal, ledgerID, err = s.assets.Grant(ctx, u.ID, delta, domain.ReasonAdminAdjust,
			operatorID, "", note)
	} else {
		bal, ledgerID, err = s.assets.Spend(ctx, u.ID, -delta, domain.ReasonAdminAdjust,
			operatorID, "", note)
	}
	if err != nil {
		return nil, err
	}
	_ = s.WriteAudit(ctx, operatorID, operatorName, "points_adjust", u.ID,
		itoa(before), itoa(bal), note, ip)
	return &AdjustResult{Balance: bal, Delta: delta, LedgerID: ledgerID}, nil
}

func abs(v int) int {
	if v < 0 {
		return -v
	}
	return v
}

func itoa(v int) string { return strconv.Itoa(v) }

package service

import (
	"context"
	"sort"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

type ShopService struct {
	db     *store.DB
	cfg    *ConfigService
	assets *AssetsService
}

func NewShopService(db *store.DB, cfg *ConfigService, assets *AssetsService) *ShopService {
	return &ShopService{db: db, cfg: cfg, assets: assets}
}

type BuyResult struct {
	OrderID   string         `json:"orderId"`
	Cost      int            `json:"cost"`
	Balance   int            `json:"balance"`
	Inventory map[string]int `json:"inventory"`
	ItemID    string         `json:"itemId"`
	Count     int            `json:"count"`
}

// BuyFood 购买食物（文档 6.3）：校验 enabled、price、余额 → 扣积分 → 加库存 → 写订单。
//
// 「扣积分 + 加库存」在一次 findOneAndUpdate 里完成，不会出现扣了钱没加货。
// 订单在资产变更之后写：万一订单写入失败，用户是「少一条记录」而不是「少了积分」，
// 失败方向对用户有利，且可用 point_ledger 反推对账。
func (s *ShopService) BuyFood(ctx context.Context, userID, foodID string, count int, requestID string) (*BuyResult, error) {
	if count <= 0 {
		return nil, domain.ErrParam("购买数量必须大于 0")
	}
	if count > 99 {
		return nil, domain.ErrParam("单次购买数量不能超过 99")
	}
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	food, ok := findFood(snap, foodID)
	if !ok {
		return nil, domain.Errf(domain.CodeSoldOut, "食物不存在：%s", foodID)
	}
	if !food.Enabled {
		return nil, domain.Errf(domain.CodeSoldOut, "食物已下架：%s", foodID)
	}

	cost := food.Price * count
	orderID := newOrderID()

	balance, _, err := s.assets.SpendAndAddItem(ctx, userID, cost, foodID, count,
		domain.ReasonBuyFood, orderID, requestID, "buy "+foodID)
	if err != nil {
		return nil, err
	}

	order := domain.Order{
		ID: newID(), OrderID: orderID, UserID: userID, Type: domain.OrderBuyFood,
		ItemID: foodID, Count: count, Cost: cost, Status: domain.OrderPaid,
		RequestID: requestID, CreatedAt: timeutil.NowMs(),
	}
	if _, err := s.db.C(store.CollOrders).InsertOne(ctx, order); err != nil {
		return nil, err
	}

	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	LogAction(ctx, s.db, userID, "", ActionBuy, foodID)
	return &BuyResult{
		OrderID: orderID, Cost: cost, Balance: balance,
		Inventory: as.Inventory, ItemID: foodID, Count: count,
	}, nil
}

type UnlockResult struct {
	OrderID string   `json:"orderId"`
	Cost    int      `json:"cost"`
	Balance int      `json:"balance"`
	SceneID string   `json:"sceneId"`
	Scenes  []string `json:"scenes"`
}

// UnlockScene 解锁场景。
func (s *ShopService) UnlockScene(ctx context.Context, userID, sceneID, requestID string) (*UnlockResult, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	sc, ok := findScene(snap, sceneID)
	if !ok {
		return nil, domain.Errf(domain.CodeSoldOut, "场景不存在：%s", sceneID)
	}
	if !sc.Enabled {
		return nil, domain.Errf(domain.CodeSoldOut, "场景已下架：%s", sceneID)
	}

	orderID := newOrderID()
	balance, _, err := s.assets.UnlockScene(ctx, userID, sceneID, sc.Price, orderID, requestID)
	if err != nil {
		return nil, err
	}

	order := domain.Order{
		ID: newID(), OrderID: orderID, UserID: userID, Type: domain.OrderUnlockScene,
		ItemID: sceneID, Count: 1, Cost: sc.Price, Status: domain.OrderPaid,
		RequestID: requestID, CreatedAt: timeutil.NowMs(),
	}
	if _, err := s.db.C(store.CollOrders).InsertOne(ctx, order); err != nil {
		return nil, err
	}
	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	LogAction(ctx, s.db, userID, "", ActionUnlock, sceneID)
	return &UnlockResult{OrderID: orderID, Cost: sc.Price, Balance: balance, SceneID: sceneID, Scenes: as.Scenes}, nil
}

// UseScene 切换当前场景。
func (s *ShopService) UseScene(ctx context.Context, userID, sceneID string) (string, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return "", err
	}
	sc, ok := findScene(snap, sceneID)
	if !ok {
		return "", domain.Errf(domain.CodeNotFound, "场景不存在：%s", sceneID)
	}
	if !sc.Enabled {
		return "", domain.Errf(domain.CodeSoldOut, "场景已下架：%s", sceneID)
	}
	if err := s.assets.SetActiveScene(ctx, userID, sceneID); err != nil {
		return "", err
	}
	return sceneID, nil
}

// ---------- 签到 ----------

type CheckInResult struct {
	Reward         int    `json:"reward"`
	Balance        int    `json:"balance"`
	AlreadyClaimed bool   `json:"alreadyClaimed"`
	Date           string `json:"date"`
	Streak         int    `json:"streak"`
}

type CheckInService struct {
	db     *store.DB
	cfg    *ConfigService
	assets *AssetsService
}

func NewCheckInService(db *store.DB, cfg *ConfigService, assets *AssetsService) *CheckInService {
	return &CheckInService{db: db, cfg: cfg, assets: assets}
}

// Claim 签到。daily_checkin 的 (user_id, date) 唯一索引就是防重手段（文档 4.2）。
func (s *CheckInService) Claim(ctx context.Context, userID string) (*CheckInResult, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	pc := snap.Points
	today := timeutil.Today()

	res := &CheckInResult{Date: today}

	// 先查是否已签，已签直接返回（不报错，客户端可正常展示）
	var prev domain.CheckIn
	err = s.db.C(store.CollCheckIn).FindOne(ctx,
		store.F("user_id", userID, "date", today)).Decode(&prev)
	if err == nil {
		as, e := s.assets.Get(ctx, userID, defaultSceneID(snap))
		if e != nil {
			return nil, e
		}
		res.AlreadyClaimed, res.Reward, res.Balance = true, prev.Reward, as.Points.Balance
		res.Streak = s.streak(ctx, userID)
		return res, nil
	}
	if err != mongo.ErrNoDocuments {
		return nil, err
	}

	if pc.CheckinDailyLimit > 0 {
		cnt, _ := s.db.C(store.CollCheckIn).CountDocuments(ctx, store.F("user_id", userID, "date", today))
		if cnt >= int64(pc.CheckinDailyLimit) {
			res.AlreadyClaimed = true
			return res, nil
		}
	}

	reward := pc.CheckinReward
	rec := domain.CheckIn{
		ID: newID(), UserID: userID, Date: today, Reward: reward,
		Settled: false, CreatedAt: timeutil.NowMs(),
	}
	// 唯一索引插成功者才发奖，并发重复签到只会有一个成功
	if _, err := s.db.C(store.CollCheckIn).InsertOne(ctx, rec); err != nil {
		if store.IsDuplicate(err) {
			as, e := s.assets.Get(ctx, userID, defaultSceneID(snap))
			if e != nil {
				return nil, e
			}
			res.AlreadyClaimed, res.Balance = true, as.Points.Balance
			res.Streak = s.streak(ctx, userID)
			return res, nil
		}
		return nil, err
	}

	balance, _, err := s.assets.Grant(ctx, userID, reward, domain.ReasonCheckIn,
		rec.ID, "", "签到 "+today)
	if err != nil {
		// 发奖失败就撤回签到记录，让用户能重试，避免「有签到无积分」
		_, _ = s.db.C(store.CollCheckIn).DeleteOne(ctx, store.F("_id", rec.ID))
		return nil, err
	}
	_, _ = s.db.C(store.CollCheckIn).UpdateOne(ctx, store.F("_id", rec.ID),
		bson.D{{Key: "$set", Value: bson.D{{Key: "settled", Value: true}}}})

	res.Reward, res.Balance = reward, balance
	res.Streak = s.streak(ctx, userID)
	return res, nil
}

// Status 只读签到状态，随 sync-state 下发。**不发奖、不写库**。
//
// 为什么不复用 claim：claim 调一次就发奖，客户端不能拿它当「查询今天签没签」用。
func (s *CheckInService) Status(ctx context.Context, userID string) (*domain.CheckInStatus, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}

	today := timeutil.Today()
	st := &domain.CheckInStatus{Date: today, Reward: snap.Points.CheckinReward}

	var prev domain.CheckIn
	err = s.db.C(store.CollCheckIn).FindOne(ctx,
		store.F("user_id", userID, "date", today)).Decode(&prev)
	switch {
	case err == nil:
		st.ClaimedToday = true
		// 已签时用当时实际发的数额，配置中途改过也能对上账
		st.Reward = prev.Reward
	case err != mongo.ErrNoDocuments:
		return nil, err
	}

	// 能不能领 = 今天还没签。
	//
	// daily_checkin 上有 (user_id, date) 唯一索引，一天最多一条记录，
	// 所以「今天签过」就等于「今天不能再领」，checkinDailyLimit 配多少都不影响。
	// Claim 里的上限分支是给「将来去掉唯一索引」留的，
	// 这里保持同一口径，不要让 status 说能领、claim 却说已签过。
	st.CanClaim = !st.ClaimedToday

	st.Streak = s.streak(ctx, userID)
	st.RecentDates = s.recentDates(ctx, userID, 7)
	return st, nil
}

// CheckInStatusResult 独立查询接口的响应。
//
// 比 domain.CheckInStatus 多两项：
//   - Balance 当前积分余额。弹窗上要显示「签到可得 100 积分」，
//     同时玩家也想知道自己现在有多少，客户端不必再发一次 sync-state。
//   - DailyLimit 当日签到次数上限（config_points.checkinDailyLimit）。
//     现在恒为 1（唯一索引决定），但后台可配，客户端显示前先问一句比较稳。
type CheckInStatusResult struct {
	*domain.CheckInStatus
	Balance    int `json:"balance"`
	DailyLimit int `json:"dailyLimit"`
}

// StatusDetail 只读签到状态 + 余额 + 上限，供独立的查询接口使用。
//
// 与 Status 的区别是它要额外读一次 assets 表拿余额。
// sync-state 那个路径不需要 —— 那边本来就会读 assets，不用读两遍。
func (s *CheckInService) StatusDetail(ctx context.Context, userID string) (*CheckInStatusResult, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	st, err := s.Status(ctx, userID)
	if err != nil {
		return nil, err
	}
	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	return &CheckInStatusResult{
		CheckInStatus: st,
		Balance:        as.Points.Balance,
		DailyLimit:     snap.Points.CheckinDailyLimit,
	}, nil
}

// recentDates 最近 n 天（含今天）签过的日期，升序。
//
// 给 7 天格子用。不让客户端拿 streak 推算：streak 的定义是「从今天往前连续」，
// 今天没签时它是 0，推不出「昨天签了、今天还没签」这种最常见的状态。
func (s *CheckInService) recentDates(ctx context.Context, userID string, n int) []string {
	if n <= 0 {
		return nil
	}
	const dayMS = 86_400_000
	now := timeutil.NowMs()
	from := timeutil.TodayAt(now - int64(n-1)*dayMS)

	cur, err := s.db.C(store.CollCheckIn).Find(ctx, bson.M{
		"user_id": userID,
		"date":    bson.M{"$gte": from},
	})
	if err != nil {
		return nil
	}
	var all []domain.CheckIn
	if err := cur.All(ctx, &all); err != nil {
		return nil
	}

	dates := make([]string, 0, len(all))
	for _, c := range all {
		dates = append(dates, c.Date)
	}
	sort.Strings(dates)   // 数据库不保证顺序，格子要按日期从左到右
	return dates
}

// streak 连续签到天数（含今天）。
func (s *CheckInService) streak(ctx context.Context, userID string) int {	cur, err := s.db.C(store.CollCheckIn).Find(ctx, store.F("user_id", userID))
	if err != nil {
		return 0
	}
	var all []domain.CheckIn
	if err := cur.All(ctx, &all); err != nil {
		return 0
	}
	have := make(map[string]bool, len(all))
	for _, c := range all {
		have[c.Date] = true
	}
	n := 0
	const dayMS = 86_400_000
	for i := 0; i < 400; i++ {
		d := timeutil.TodayAt(timeutil.NowMs() - int64(i)*dayMS)
		if !have[d] {
			break
		}
		n++
	}
	return n
}

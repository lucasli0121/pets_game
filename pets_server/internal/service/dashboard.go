package service

import (
	"context"

	"go.mongodb.org/mongo-driver/v2/bson"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

// 行为埋点类型（写 action_log，供 A10 看板的行为指标）
const (
	ActionFeed     = "feed"
	ActionBuy      = "buy"
	ActionUnlock   = "unlock"
	ActionSwitch   = "switch"
	ActionInteract = "interact"
	ActionAdReward = "ad_reward"
)

// LogAction 记录行为，用于看板统计。失败不影响主流程。
func LogAction(ctx context.Context, db *store.DB, userID, petID, action, itemID string) {
	type doc struct {
		ID        string `bson:"_id"`
		UserID    string `bson:"user_id"`
		PetID     string `bson:"pet_id,omitempty"`
		Action    string `bson:"action"`
		ItemID    string `bson:"item_id,omitempty"`
		CreatedAt int64  `bson:"created_at"`
	}
	_, _ = db.C(store.CollActionLog).InsertOne(ctx, doc{
		ID: newID(), UserID: userID, PetID: petID, Action: action,
		ItemID: itemID, CreatedAt: timeutil.NowMs(),
	})
}

// LogDailyActive 记录活跃日，(user_id,date) 唯一，重复登录只算一次。
func LogDailyActive(ctx context.Context, db *store.DB, userID string) {
	type doc struct {
		ID     string `bson:"_id"`
		UserID string `bson:"user_id"`
		Date   string `bson:"date"`
	}
	_, _ = db.C(store.CollDailyActive).InsertOne(ctx, doc{
		ID: newID(), UserID: userID, Date: timeutil.Today(),
	})
}

// ---------- 看板 ----------

type ReasonStat struct {
	Reason string `json:"reason"`
	Total  int    `json:"total"`
	Count  int    `json:"count"`
}

type BalanceBucket struct {
	Label string `json:"label"`
	Count int    `json:"count"`
}

type UsersMetrics struct {
	TotalUsers  int64   `json:"totalUsers"`
	NewToday    int64   `json:"newToday"`
	NewInRange  int64   `json:"newInRange"`
	DAU         int64   `json:"dau"`
	D1Retention float64 `json:"d1Retention"`
	D7Retention float64 `json:"d7Retention"`
	ActivePets  int64   `json:"activePets"`
}

type AdsMetrics struct {
	RewardedRequests  int64   `json:"rewardedRequests"`
	RewardedCompleted int64   `json:"rewardedCompleted"`
	CompletionRate    float64 `json:"completionRate"`
	AvgPerUser        float64 `json:"avgPerUser"`
	InterstitialViews int64   `json:"interstitialViews"`
	RewardedFailRate  float64 `json:"rewardedFailRate"`
}

type PointsMetrics struct {
	TotalEarned   int64           `json:"totalEarned"`
	TotalSpent    int64           `json:"totalSpent"`
	BalanceSum    int64           `json:"balanceSum"`
	InflationRate float64         `json:"inflationRate"`
	ByReason      []ReasonStat    `json:"byReason"`
	Buckets       []BalanceBucket `json:"buckets"`
}

type BehaviorMetrics struct {
	FeedCount     int64 `json:"feedCount"`
	BuyCount      int64 `json:"buyCount"`
	UnlockCount   int64 `json:"unlockCount"`
	SwitchCount   int64 `json:"switchCount"`
	InteractCount int64 `json:"interactCount"`
}

type Dashboard struct {
	GeneratedAt int64           `json:"generatedAt"`
	RangeDays   int             `json:"rangeDays"`
	Users       UsersMetrics    `json:"users"`
	Ads         AdsMetrics      `json:"ads"`
	Points      PointsMetrics   `json:"points"`
	Behavior    BehaviorMetrics `json:"behavior"`
}

// Dashboard 汇总看板指标（文档 7.3）。
//
// 口径说明（避免二次解读出错）：
//   - DAU 取自 daily_active 表（登录即记一条，(user_id,date) 唯一），不是 last_active_at。
//   - 次日/7 日留存 = 注册日为 D-1 / D-7 的用户中，今天有活跃记录的比例。
//   - 活跃宠物数 = 今天有活跃记录的用户所拥有的宠物数（不是「宠物自身活跃」，客户端不上报）。
//   - 积分通胀率 = 累计产出 / 累计消耗，>1 表示产出大于消耗。
func (s *AdminService) Dashboard(ctx context.Context, rangeDays int) (*Dashboard, error) {
	if rangeDays <= 0 || rangeDays > 90 {
		rangeDays = 7
	}
	now := timeutil.NowMs()
	today := timeutil.Today()
	todayStart := timeutil.DayStart(now)
	rangeStart := timeutil.DayStart(now - int64(rangeDays-1)*86_400_000)

	d := &Dashboard{GeneratedAt: now, RangeDays: rangeDays}

	// ---- 用户 ----
	d.Users.TotalUsers, _ = s.db.C(store.CollUsers).CountDocuments(ctx, bson.M{})
	d.Users.NewToday, _ = s.db.C(store.CollUsers).CountDocuments(ctx,
		store.F("created_at", bson.M{"$gte": todayStart}))
	d.Users.NewInRange, _ = s.db.C(store.CollUsers).CountDocuments(ctx,
		store.F("created_at", bson.M{"$gte": rangeStart}))
	d.Users.DAU, _ = s.db.C(store.CollDailyActive).CountDocuments(ctx,
		store.F("date", today))

	d.Users.D1Retention = s.retention(ctx, timeutil.TodayAt(now-86_400_000), today)
	d.Users.D7Retention = s.retention(ctx, timeutil.TodayAt(now-7*86_400_000), today)

	// 活跃宠物：今天活跃用户拥有的宠物
	activeUsers, err := s.activeUserIDs(ctx, today)
	if err == nil && len(activeUsers) > 0 {
		d.Users.ActivePets, _ = s.db.C(store.CollPets).CountDocuments(ctx,
			store.F("user_id", bson.M{"$in": activeUsers}))
	}

	// ---- 广告 ----
	adFilter := bson.M{"created_at": bson.M{"$gte": rangeStart}}
	d.Ads.RewardedRequests, _ = s.db.C(store.CollAdEvents).CountDocuments(ctx,
		mergeM(adFilter, bson.M{"ad_type": domain.AdTypeRewarded}))
	d.Ads.RewardedCompleted, _ = s.db.C(store.CollAdEvents).CountDocuments(ctx,
		mergeM(adFilter, bson.M{"ad_type": domain.AdTypeRewarded, "completed": true}))
	d.Ads.InterstitialViews, _ = s.db.C(store.CollAdEvents).CountDocuments(ctx,
		mergeM(adFilter, bson.M{"ad_type": domain.AdTypeInterstitial}))
	if d.Ads.RewardedRequests > 0 {
		d.Ads.CompletionRate = round2(float64(d.Ads.RewardedCompleted) / float64(d.Ads.RewardedRequests))
		failed, _ := s.db.C(store.CollAdEvents).CountDocuments(ctx,
			mergeM(adFilter, bson.M{"ad_type": domain.AdTypeRewarded, "rewarded": false}))
		d.Ads.RewardedFailRate = round2(float64(failed) / float64(d.Ads.RewardedRequests))
	}
	if d.Users.DAU > 0 {
		d.Ads.AvgPerUser = round2(float64(d.Ads.RewardedRequests) / float64(d.Users.DAU))
	}

	// ---- 积分 ----
	s.aggregatePoints(ctx, d, rangeStart)

	// ---- 行为 ----
	bFilter := bson.M{"created_at": bson.M{"$gte": rangeStart}}
	d.Behavior.FeedCount = s.countAction(ctx, bFilter, ActionFeed)
	d.Behavior.BuyCount = s.countAction(ctx, bFilter, ActionBuy)
	d.Behavior.UnlockCount = s.countAction(ctx, bFilter, ActionUnlock)
	d.Behavior.SwitchCount = s.countAction(ctx, bFilter, ActionSwitch)
	d.Behavior.InteractCount = s.countAction(ctx, bFilter, ActionInteract)

	return d, nil
}

func (s *AdminService) countAction(ctx context.Context, base bson.M, action string) int64 {
	n, _ := s.db.C(store.CollActionLog).CountDocuments(ctx, mergeM(base, bson.M{"action": action}))
	return n
}

func (s *AdminService) activeUserIDs(ctx context.Context, date string) ([]string, error) {
	cur, err := s.db.C(store.CollDailyActive).Find(ctx, store.F("date", date))
	if err != nil {
		return nil, err
	}
	type row struct {
		UserID string `bson:"user_id"`
	}
	var rows []row
	if err := cur.All(ctx, &rows); err != nil {
		return nil, err
	}
	out := make([]string, 0, len(rows))
	for _, r := range rows {
		out = append(out, r.UserID)
	}
	return out, nil
}

// retention 计算「注册日为 cohortDate 的用户里，date 当天有活跃记录」的比例。
func (s *AdminService) retention(ctx context.Context, cohortDate, date string) float64 {
	start, perr := timeutil.ParseDateMs(cohortDate)
	if perr != nil {
		return 0
	}
	end := start + 86_400_000

	cur, err := s.db.C(store.CollUsers).Find(ctx,
		store.F("registered_at", bson.M{"$gte": start, "$lt": end}))
	if err != nil {
		return 0
	}
	type row struct {
		ID string `bson:"_id"`
	}
	var cohort []row
	if err := cur.All(ctx, &cohort); err != nil || len(cohort) == 0 {
		return 0
	}
	ids := make([]string, 0, len(cohort))
	for _, c := range cohort {
		ids = append(ids, c.ID)
	}
	retained, _ := s.db.C(store.CollDailyActive).CountDocuments(ctx,
		store.F("date", date, "user_id", bson.M{"$in": ids}))
	return round2(float64(retained) / float64(len(cohort)))
}

// aggregatePoints 积分总量、来源占比、余额分布。
func (s *AdminService) aggregatePoints(ctx context.Context, d *Dashboard, rangeStart int64) {
	type sumRow struct {
		TotalEarned int64 `bson:"earned"`
		TotalSpent  int64 `bson:"spent"`
		BalanceSum  int64 `bson:"balance"`
		Users       int64 `bson:"n"`
	}
	cur, err := s.db.C(store.CollAssets).Aggregate(ctx, bson.A{
		bson.M{"$group": bson.M{
			"_id":     nil,
			"earned":  bson.M{"$sum": "$points.total_earned"},
			"spent":   bson.M{"$sum": "$points.total_spent"},
			"balance": bson.M{"$sum": "$points.balance"},
			"n":       bson.M{"$sum": 1},
		}},
	})
	if err == nil {
		var rows []sumRow
		if err := cur.All(ctx, &rows); err == nil && len(rows) > 0 {
			d.Points.TotalEarned = rows[0].TotalEarned
			d.Points.TotalSpent = rows[0].TotalSpent
			d.Points.BalanceSum = rows[0].BalanceSum
		}
	}
	if d.Points.TotalSpent > 0 {
		d.Points.InflationRate = round2(float64(d.Points.TotalEarned) / float64(d.Points.TotalSpent))
	} else if d.Points.TotalEarned > 0 {
		d.Points.InflationRate = float64(d.Points.TotalEarned) // 分母为 0：只产出没消耗
	}

	// 来源占比
	type reasonRow struct {
		Reason string `bson:"_id"`
		Total  int64  `bson:"total"`
		Count  int64  `bson:"count"`
	}
	cur2, err := s.db.C(store.CollLedger).Aggregate(ctx, bson.A{
		bson.M{"$match": bson.M{"created_at": bson.M{"$gte": rangeStart}}},
		bson.M{"$group": bson.M{
			"_id":   "$reason",
			"total": bson.M{"$sum": "$delta"},
			"count": bson.M{"$sum": 1},
		}},
		bson.M{"$sort": bson.M{"total": -1}},
	})
	if err == nil {
		var rows []reasonRow
		if err := cur2.All(ctx, &rows); err == nil {
			for _, r := range rows {
				d.Points.ByReason = append(d.Points.ByReason, ReasonStat{
					Reason: r.Reason, Total: int(r.Total), Count: int(r.Count),
				})
			}
		}
	}

	// 余额分布
	type bucketRow struct {
		ID    any   `bson:"_id"`
		Count int64 `bson:"count"`
	}
	cur3, err := s.db.C(store.CollAssets).Aggregate(ctx, bson.A{
		bson.M{"$bucket": bson.M{
			"groupBy":    "$points.balance",
			"boundaries": bson.A{0, 100, 500, 1000, 5000},
			"default":    "5000+",
			"output":     bson.M{"count": bson.M{"$sum": 1}},
		}},
	})
	if err == nil {
		var rows []bucketRow
		if err := cur3.All(ctx, &rows); err == nil {
			labels := []string{"0-99", "100-499", "500-999", "1000-4999", "5000+"}
			for _, r := range rows {
				label := ""
				switch v := r.ID.(type) {
				case int32:
					label = bucketLabel(int(v), labels)
				case int64:
					label = bucketLabel(int(v), labels)
				case float64:
					label = bucketLabel(int(v), labels)
				default:
					label = "5000+"
				}
				d.Points.Buckets = append(d.Points.Buckets, BalanceBucket{Label: label, Count: int(r.Count)})
			}
		}
	}
}

func bucketLabel(lower int, labels []string) string {
	switch {
	case lower < 100:
		return labels[0]
	case lower < 500:
		return labels[1]
	case lower < 1000:
		return labels[2]
	case lower < 5000:
		return labels[3]
	default:
		return labels[4]
	}
}

func mergeM(base bson.M, extra bson.M) bson.M {
	out := bson.M{}
	for k, v := range base {
		out[k] = v
	}
	for k, v := range extra {
		out[k] = v
	}
	return out
}

func round2(v float64) float64 {
	return float64(int(v*100+0.5)) / 100
}

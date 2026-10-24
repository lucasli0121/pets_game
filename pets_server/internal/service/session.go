package service

// 登录会话与活跃统计。
//
// 口径说明（管理端 A8「用户资料」用到的几个指标都出自这里）：
//
// 登录次数：每次 /api/v1/login 成功就 +1，含重复登录。
//
// 活跃天数：用户有过登录的 UTC+8 自然日数，取自 daily_active 表的条数。
// 「平均每天 X」一律除以它，不用「注册至今天数」——
// 后者会把未活跃的日子算进分母，新用户首日均分被大幅拉低，无法反映真实使用强度。
//
// 最大/累计在线时长：客户端还没上报心跳，拿不到真实在线时长，
// 所以用**相邻两次登录的时间差**近似一次会话：本次登录时结算上一次会话。
// 间隔超过 sessionCapMs（默认 2 小时）就按 2 小时算——
// 用户不可能开着 App 睡一觉还没退出，不封顶会把「第二天再来」算成一次超长在线。
// 这个口径的已知偏差：中途直接杀进程且当天不再登录时，这次会话不计时长。
//
// 想改成精确值需要客户端接 /api/v1/session/heartbeat（心跳间隔 5 分钟、15 分钟无心跳视为离线），
// 届时只要把这里换掉，users 上的字段结构不用动。

import (
	"context"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

// sessionCapMs 单次会话时长上限，2 小时。
const sessionCapMs = int64(2 * 3600 * 1000)

// Session 结算本次登录：累计登录次数、活跃天数、结算上一次会话时长。
//
// 必须在 Login 里先于返回调用。失败不阻断登录——统计挂了不该让用户进不了游戏。
func Session(ctx context.Context, db *store.DB, userID string, now int64) {
	col := db.C(store.CollUsers)

	var u struct {
		LoginCount     int   `bson:"login_count"`
		LastSessionEnd int64 `bson:"last_session_end"`
	}
	if err := col.FindOne(ctx, store.F("_id", userID)).Decode(&u); err != nil {
		return
	}

	set := bson.D{
		{Key: "last_login_at", Value: now},
		{Key: "last_active_at", Value: now},
		{Key: "login_count", Value: u.LoginCount + 1},
		{Key: "last_session_end", Value: now},
	}

	// 结算上一次会话：上次登录开、这次登录关。
	if u.LastSessionEnd > 0 && now > u.LastSessionEnd {
		d := now - u.LastSessionEnd
		if d > sessionCapMs {
			d = sessionCapMs
		}
		_, _ = col.UpdateOne(ctx, store.F("_id", userID), bson.D{
			{Key: "$inc", Value: bson.D{{Key: "total_online_ms", Value: d}}},
			{Key: "$max", Value: bson.D{{Key: "max_online_ms", Value: d}}},
		})
	}
	_, _ = col.UpdateOne(ctx, store.F("_id", userID), bson.D{{Key: "$set", Value: set}})

	// 活跃天数：daily_active 有 (user_id, date) 唯一索引，重复登录不会重复计数。
	// 只在首次登录（login_count 从 0 变 1）时 +1，避免依赖 upsert 结果判断。
	if u.LoginCount == 0 {
		_, _ = db.C(store.CollUsers).UpdateOne(ctx, store.F("_id", userID),
			bson.D{{Key: "$inc", Value: bson.D{{Key: "active_days", Value: 1}}}})
	}
}

// ActiveDaysOf 用户有过登录的自然日数。以 daily_active 为准（唯一索引保证一天一条），
// 读不到时回落到 users.active_days。
func ActiveDaysOf(ctx context.Context, db *store.DB, userID string) int {
	n, err := db.C(store.CollDailyActive).CountDocuments(ctx, store.F("user_id", userID))
	if err != nil || n == 0 {
		var u struct {
			ActiveDays int `bson:"active_days"`
		}
		if e := db.C(store.CollUsers).FindOne(ctx, store.F("_id", userID),
			options.FindOne().SetProjection(bson.M{"active_days": 1})).Decode(&u); e != nil {
			return 0
		}
		return u.ActiveDays
	}
	return int(n)
}

// UserStats 用户行为统计，供 A8 用户资料展示。
//
// 所有「日均」的分母都是活跃天数（见文件头口径说明），活跃天数为 0 时返回 0 而不是除零。
type UserStats struct {
	ActiveDays        int     `json:"activeDays"`
	LoginCount        int     `json:"loginCount"`
	LoginPerDay       float64 `json:"loginPerDay"`
	TotalOnlineMs     int64   `json:"totalOnlineMs"`
	MaxOnlineMs       int64   `json:"maxOnlineMs"`
	TotalOnlineMin    float64 `json:"totalOnlineMinutes"`
	MaxOnlineMin      float64 `json:"maxOnlineMinutes"`
	FeedCount         int64   `json:"feedCount"`
	FeedPerDay        float64 `json:"feedPerDay"`
	InteractCount     int64   `json:"interactCount"`
	PointsSpentTotal  int64   `json:"pointsSpentTotal"`
	PointsSpentPerDay float64 `json:"pointsSpentPerDay"`
	PetsOwned         int64   `json:"petsOwned"`
	FirstActiveAt     int64   `json:"firstActiveAt"`
	LastActiveAt      int64   `json:"lastActiveAt"`
}

// StatsOf 汇总一个用户的行为统计。
func StatsOf(ctx context.Context, db *store.DB, userID string) *UserStats {
	st := &UserStats{}
	st.ActiveDays = ActiveDaysOf(ctx, db, userID)

	var u struct {
		LoginCount    int   `bson:"login_count"`
		TotalOnlineMs int64 `bson:"total_online_ms"`
		MaxOnlineMs   int64 `bson:"max_online_ms"`
		LastActiveAt  int64 `bson:"last_active_at"`
	}
	_ = db.C(store.CollUsers).FindOne(ctx, store.F("_id", userID)).Decode(&u)
	st.LoginCount = u.LoginCount
	st.TotalOnlineMs = u.TotalOnlineMs
	st.MaxOnlineMs = u.MaxOnlineMs
	st.LastActiveAt = u.LastActiveAt
	st.TotalOnlineMin = round1(float64(u.TotalOnlineMs) / 60000)
	st.MaxOnlineMin = round1(float64(u.MaxOnlineMs) / 60000)

	// 首次活跃日：daily_active 里最早的 date。
	var first struct {
		Date string `bson:"date"`
	}
	if e := db.C(store.CollDailyActive).FindOne(ctx, store.F("user_id", userID),
		options.FindOne().SetSort(store.D("date", 1)).SetProjection(bson.M{"date": 1})).Decode(&first); e == nil {
		if ms, err := timeutil.ParseDateMs(first.Date); err == nil {
			st.FirstActiveAt = ms
		}
	}

	if d := int64(st.ActiveDays); d > 0 {
		st.LoginPerDay = round2(float64(st.LoginCount) / float64(d))
	}

	feed, _ := db.C(store.CollActionLog).CountDocuments(ctx,
		store.F("user_id", userID, "action", ActionFeed))
	st.FeedCount = feed
	if st.ActiveDays > 0 {
		st.FeedPerDay = round2(float64(feed) / float64(st.ActiveDays))
	}

	inter, _ := db.C(store.CollActionLog).CountDocuments(ctx,
		store.F("user_id", userID, "action", ActionInteract))
	st.InteractCount = inter

	// 花费积分：point_ledger 里所有负数变动求和。
	// 买食物、解锁场景、改性格都走这里，所以这是「总消耗」而非只有喂养。
	// 人工调整的负数也会算进来——它同样是花出去的钱。
	spent, _ := spentPoints(ctx, db, userID)
	st.PointsSpentTotal = spent
	if st.ActiveDays > 0 {
		st.PointsSpentPerDay = round2(float64(spent) / float64(st.ActiveDays))
	}

	st.PetsOwned, _ = db.C(store.CollPets).CountDocuments(ctx, store.F("user_id", userID))
	return st
}

func spentPoints(ctx context.Context, db *store.DB, userID string) (int64, error) {
	cur, err := db.C(store.CollLedger).Find(ctx,
		store.F("user_id", userID, "delta", bson.M{"$lt": 0}),
		options.Find().SetProjection(bson.M{"delta": 1}))
	if err != nil {
		return 0, err
	}
	var rows []struct {
		Delta int `bson:"delta"`
	}
	if err := cur.All(ctx, &rows); err != nil {
		return 0, err
	}
	var sum int64
	for _, r := range rows {
		sum += int64(-r.Delta) // delta 为负，取绝对值
	}
	return sum, nil
}

// FeedRecord 一条喂养记录（管理端 A8 展示）。
//
// 花费积分是**按该食物当前单价估算**的：喂养只扣库存，积分在购买时已扣。
// 运营改价后历史记录会与当时实付不一致，这是已知且接受的取舍——
// 真要精确得做库存批次先进先出，V1 不值得。
type FeedRecord struct {
	ID        string `bson:"_id" json:"id"`
	UserID    string `bson:"user_id" json:"userId"`
	PetID     string `bson:"pet_id,omitempty" json:"petId,omitempty"`
	PetName   string `bson:"-" json:"petName,omitempty"`
	FoodID    string `bson:"item_id" json:"foodId"`
	FoodName  string `bson:"-" json:"foodName"`
	Cost      int    `bson:"-" json:"cost"`
	Satiety   int    `bson:"-" json:"satiety"`
	Happiness int    `bson:"-" json:"happiness"`
	Exp       int    `bson:"-" json:"exp"`
	CreatedAt int64  `bson:"created_at" json:"createdAt"`
}

// FeedRecords 取最近若干条喂养记录，并把 action_log 里的 item_id 补成食物中文名与单价。
func FeedRecords(ctx context.Context, db *store.DB, userID string, limit int64, foods map[string]int, names map[string]string, pets map[string]string) ([]FeedRecord, error) {
	if limit <= 0 || limit > 200 {
		limit = 50
	}
	cur, err := db.C(store.CollActionLog).Find(ctx,
		store.F("user_id", userID, "action", ActionFeed),
		options.Find().SetSort(store.D("created_at", -1)).SetLimit(limit))
	if err != nil {
		return nil, err
	}
	var out []FeedRecord
	if err := cur.All(ctx, &out); err != nil {
		return nil, err
	}
	for i := range out {
		out[i].FoodName = names[out[i].FoodID]
		if out[i].FoodName == "" {
			out[i].FoodName = out[i].FoodID // 食物已下架或删除时至少显示 ID
		}
		out[i].Cost = foods[out[i].FoodID]
		out[i].PetName = pets[out[i].PetID]
	}
	return out, nil
}

// FoodMeta 取当前生效配置里全部食物的价格与名称，供喂养记录补全用。
func FoodMeta(ctx context.Context, db *store.DB) (map[string]int, map[string]string) {
	prices := map[string]int{}
	names := map[string]string{}
	cur, err := db.C(store.CollConfigFood).Find(ctx, bson.M{})
	if err != nil {
		return prices, names
	}
	var rows []struct {
		ID    string `bson:"id"`
		Name  string `bson:"name"`
		Price int    `bson:"price"`
	}
	if cur.All(ctx, &rows) != nil {
		return prices, names
	}
	for _, r := range rows {
		prices[r.ID] = r.Price
		names[r.ID] = r.Name
	}
	return prices, names
}

// PetNameMap 用户名下宠物 id → 名字。
func PetNameMap(ctx context.Context, db *store.DB, userID string) map[string]string {
	out := map[string]string{}
	cur, err := db.C(store.CollPets).Find(ctx, store.F("user_id", userID),
		options.Find().SetProjection(bson.M{"name": 1}))
	if err != nil {
		return out
	}
	var rows []struct {
		ID   string `bson:"_id"`
		Name string `bson:"name"`
	}
	if cur.All(ctx, &rows) != nil {
		return out
	}
	for _, r := range rows {
		out[r.ID] = r.Name
	}
	return out
}

func round1(v float64) float64 {
	return float64(int64(v*10+0.5)) / 10
}

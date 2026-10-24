package test

// A8「用户资料」的行为统计与喂养记录测试。
//
// 三个统计口径容易算错，分别用独立用例钉住：
//  1. 登录次数每次登录都 +1，但活跃天数一天只 +1（同一天反复登录不能把活跃天数刷上去）
//  2. 在线时长按登录间隔估算，且单次封顶 2 小时
//  3. 喂养记录的花费积分按当前单价估算

import (
	"context"
	"testing"

	"go.mongodb.org/mongo-driver/v2/bson"

	"pets_server/internal/service"
	"pets_server/internal/timeutil"
)

const hour = int64(3600 * 1000)

// TestStats_登录次数与活跃天数 同一天登录 3 次：loginCount=3、activeDays=1。
func TestStats_登录次数与活跃天数(t *testing.T) {
	a, srv := newApp(t, nil)
	tok, data := login(t, srv, "st01")
	userID, _ := data["user"].(map[string]any)
	uid, _ := userID["id"].(string)
	if uid == "" {
		t.Fatalf("登录未返回用户 id: %v", data)
	}
	_ = tok

	// 同一天再登录两次
	login(t, srv, "st01")
	login(t, srv, "st01")

	ctx := context.Background()
	var u struct {
		LoginCount int `bson:"login_count"`
		ActiveDays int `bson:"active_days"`
	}
	if err := a.DB.C("users").FindOne(ctx, bson.M{"_id": uid}).Decode(&u); err != nil {
		t.Fatalf("读用户失败: %v", err)
	}
	if u.LoginCount != 3 {
		t.Fatalf("同一天登录 3 次，loginCount 应为 3，实际 %d", u.LoginCount)
	}
	if u.ActiveDays != 1 {
		t.Fatalf("同一天登录 3 次，activeDays 应为 1（不能按登录次数刷），实际 %d", u.ActiveDays)
	}

	// daily_active 是权威来源，应也只有 1 条。
	n, _ := a.DB.C("daily_active").CountDocuments(ctx, bson.M{"user_id": uid})
	if n != 1 {
		t.Fatalf("daily_active 应只有 1 条，实际 %d", n)
	}

	st := service.StatsOf(ctx, a.DB, uid)
	if st.LoginCount != 3 {
		t.Errorf("stats.loginCount 应为 3，实际 %d", st.LoginCount)
	}
	if st.ActiveDays != 1 {
		t.Errorf("stats.activeDays 应为 1，实际 %d", st.ActiveDays)
	}
	if st.LoginPerDay != 3 {
		t.Errorf("stats.loginPerDay 应为 3（3 次 ÷ 1 天），实际 %v", st.LoginPerDay)
	}
}

// TestStats_在线时长按登录间隔估算 间隔 30 分钟 → 累计 30 分钟。
func TestStats_在线时长按登录间隔估算(t *testing.T) {
	a, srv := newApp(t, nil)
	_, data := login(t, srv, "st02")
	userID, _ := data["user"].(map[string]any)
	uid, _ := userID["id"].(string)

	// 把上次会话结束时间往前挪 30 分钟，再登录一次触发结算
	ctx := context.Background()
	_, err := a.DB.C("users").UpdateOne(ctx, bson.M{"_id": uid},
		bson.M{"$set": bson.M{"last_session_end": timeutil.NowMs() - 30*60*1000}})
	if err != nil {
		t.Fatalf("改 last_session_end 失败: %v", err)
	}
	login(t, srv, "st02")

	st := service.StatsOf(ctx, a.DB, uid)
	if st.TotalOnlineMin != 30 {
		t.Fatalf("累计在线应为 30 分钟，实际 %v", st.TotalOnlineMin)
	}
	if st.MaxOnlineMin != 30 {
		t.Fatalf("最大在线应为 30 分钟，实际 %v", st.MaxOnlineMin)
	}
}

// TestStats_在线时长封顶两小时 间隔 10 小时也只能记 2 小时。
func TestStats_在线时长封顶两小时(t *testing.T) {
	a, srv := newApp(t, nil)
	_, data := login(t, srv, "st03")
	userID, _ := data["user"].(map[string]any)
	uid, _ := userID["id"].(string)

	ctx := context.Background()
	_, err := a.DB.C("users").UpdateOne(ctx, bson.M{"_id": uid},
		bson.M{"$set": bson.M{"last_session_end": timeutil.NowMs() - 10*hour}})
	if err != nil {
		t.Fatalf("改 last_session_end 失败: %v", err)
	}
	login(t, srv, "st03")

	st := service.StatsOf(ctx, a.DB, uid)
	if st.MaxOnlineMin != 120 {
		t.Fatalf("间隔 10 小时应封顶为 120 分钟，实际 %v", st.MaxOnlineMin)
	}
}

// TestStats_最大在线取历史最大值 两次会话，短的不该覆盖长的。
//
// 这条专门盯 $max 是否真的生效——$inc 与 $max 混用是最容易写错的地方。
func TestStats_最大在线取历史最大值(t *testing.T) {
	a, srv := newApp(t, nil)
	_, data := login(t, srv, "st04")
	userID, _ := data["user"].(map[string]any)
	uid, _ := userID["id"].(string)
	ctx := context.Background()

	// 第一次：60 分钟
	_, _ = a.DB.C("users").UpdateOne(ctx, bson.M{"_id": uid},
		bson.M{"$set": bson.M{"last_session_end": timeutil.NowMs() - 60*60*1000}})
	login(t, srv, "st04")
	st := service.StatsOf(ctx, a.DB, uid)
	if st.MaxOnlineMin != 60 {
		t.Fatalf("第一次会话 60 分钟后最大在线应为 60，实际 %v", st.MaxOnlineMin)
	}

	// 第二次：10 分钟，不该把 60 顶掉
	_, _ = a.DB.C("users").UpdateOne(ctx, bson.M{"_id": uid},
		bson.M{"$set": bson.M{"last_session_end": timeutil.NowMs() - 10*60*1000}})
	login(t, srv, "st04")
	st = service.StatsOf(ctx, a.DB, uid)
	if st.MaxOnlineMin != 60 {
		t.Fatalf("更短的会话不该覆盖最大值，期望仍为 60，实际 %v", st.MaxOnlineMin)
	}
	if st.TotalOnlineMin != 70 {
		t.Fatalf("累计应为 60+10=70 分钟，实际 %v", st.TotalOnlineMin)
	}
}

// TestStats_喂养次数与日均花费 买 2 次牛肉罐头喂 2 次。
func TestStats_喂养次数与日均花费(t *testing.T) {
	a, srv := newApp(t, nil)
	tok, data := login(t, srv, "st05")
	userID, _ := data["user"].(map[string]any)
	uid, _ := userID["id"].(string)
	petID := adopt(t, srv, tok)

	// 牛肉罐头单价 10，买 2 个喂 2 次 → 总花费 20
	for i := 0; i < 2; i++ {
		call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
			"requestId": "buy-st05-" + string(rune('a'+i)), "foodId": "food_beef", "count": 1})
		call(t, srv, "POST", "/api/v1/pet/feed", tok, map[string]any{
			"requestId": "feed-st05-" + string(rune('a'+i)), "petId": petID, "foodId": "food_beef"})
	}

	ctx := context.Background()
	st := service.StatsOf(ctx, a.DB, uid)
	if st.FeedCount != 2 {
		t.Fatalf("喂养次数应为 2，实际 %d", st.FeedCount)
	}
	if st.FeedPerDay != 2 {
		t.Errorf("日均喂养应为 2（2 次 ÷ 1 天），实际 %v", st.FeedPerDay)
	}
	if st.PointsSpentTotal != 20 {
		t.Fatalf("总花费应为 20（牛肉罐头 10 × 2），实际 %d", st.PointsSpentTotal)
	}
	if st.PointsSpentPerDay != 20 {
		t.Errorf("日均花费应为 20，实际 %v", st.PointsSpentPerDay)
	}
}

// TestStats_无数据用户不崩 新号全是零值，且日均为 0 而不是 NaN。
func TestStats_无数据用户不崩(t *testing.T) {
	a, srv := newApp(t, nil)
	_, data := login(t, srv, "st06")
	userID, _ := data["user"].(map[string]any)
	uid, _ := userID["id"].(string)

	st := service.StatsOf(context.Background(), a.DB, uid)
	if st.ActiveDays != 1 {
		t.Errorf("登录过一次活跃天数应为 1，实际 %d", st.ActiveDays)
	}
	if st.FeedPerDay != 0 || st.PointsSpentPerDay != 0 || st.LoginPerDay != 1 {
		t.Errorf("零值场景不应出现异常数字：%+v", st)
	}
}

// TestStats_管理端详情返回统计与喂养记录 验证真机链路与字段名。
func TestStats_管理端详情返回统计与喂养记录(t *testing.T) {
	_, srv := newApp(t, nil)
	adminTok := adminLogin(t, srv)
	tok, _ := login(t, srv, "st07")
	petID := adopt(t, srv, tok)

	call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
		"requestId": "buy-st07", "foodId": "food_beef", "count": 1})
	call(t, srv, "POST", "/api/v1/pet/feed", tok, map[string]any{
		"requestId": "feed-st07", "petId": petID, "foodId": "food_beef"})

	// 列表搜索：按 openid 精确匹配。
	// 注意开发登录会加 "dev_" 前缀（见 api/client.go），库里存的是 dev_st07。
	env, data := call(t, srv, "GET", "/api/admin/users?keyword=dev_st07&size=5", adminTok, nil)
	mustOK(t, env, data, "searchUsers")
	list, _ := data["list"].([]any)
	if len(list) != 1 {
		t.Fatalf("按 openid 精确搜索应只命中 1 条，实际 %d 条", len(list))
	}
	u, _ := list[0].(map[string]any)
	for _, k := range []string{"activeDays", "loginCount", "loginPerDay",
		"maxOnlineMinutes", "feedPerDay", "pointsSpentPerDay"} {
		if _, ok := u[k]; !ok {
			t.Errorf("列表项缺字段 %q", k)
		}
	}

	env, data = call(t, srv, "GET", "/api/admin/users/dev_st07", adminTok, nil)
	mustOK(t, env, data, "userDetail")

	stats, ok := data["stats"].(map[string]any)
	if !ok {
		t.Fatal("详情应返回 stats")
	}
	for _, k := range []string{"activeDays", "loginCount", "loginPerDay",
		"totalOnlineMinutes", "maxOnlineMinutes", "feedCount", "feedPerDay",
		"pointsSpentTotal", "pointsSpentPerDay"} {
		if _, ok := stats[k]; !ok {
			t.Errorf("stats 缺字段 %q", k)
		}
	}

	feeds, ok := data["feedRecords"].([]any)
	if !ok || len(feeds) == 0 {
		t.Fatalf("详情应返回至少 1 条喂养记录，实际 %v", data["feedRecords"])
	}
	f0, _ := feeds[0].(map[string]any)
	for _, k := range []string{"createdAt", "foodId", "foodName", "cost"} {
		if _, ok := f0[k]; !ok {
			t.Errorf("喂养记录缺字段 %q", k)
		}
	}
	if f0["foodName"] != "牛肉罐头" {
		t.Errorf("喂养记录应显示中文食物名，实际 %v", f0["foodName"])
	}
	if num(f0["cost"]) != 10 {
		t.Errorf("牛肉罐头单价 10，记录里的花费应为 10，实际 %v", f0["cost"])
	}
}

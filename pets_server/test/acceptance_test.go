package test

import (
	"context"
	"fmt"
	"net/http/httptest"
	"testing"
	"time"

	"go.mongodb.org/mongo-driver/v2/bson"

	"pets_server/internal/app"
	"pets_server/internal/auth"
	"pets_server/internal/config"
	"pets_server/internal/timeutil"
)

// 验收用例对应《后台与管理端需求文档》第 10 节的 9 条验收标准，
// 另补幂等、越权、角色权限、频控、互动上限等边界。

// 验收 1：客户端改包把积分改成 999999，服务端仍按真实余额判定，超支被拒。
func Test01_超支被拒(t *testing.T) {
	a, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u01")
	adopt(t, srv, tok)

	env, data := call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
		"requestId": "r-overdraft", "foodId": "food_cake", "count": 99,
	})
	if env.Code != 2001 {
		t.Fatalf("期望积分不足(2001)，实际 code=%d msg=%s", env.Code, env.Msg)
	}
	_ = data

	// 余额必须原封不动
	_, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	points := sync["points"].(map[string]any)
	if num(points["balance"]) != 500 {
		t.Fatalf("超支被拒后余额应仍为 500，实际 %v", points["balance"])
	}
	_ = a
}

// 验收 2：同一笔购买重复提交 5 次，只扣一次积分、库存只加一次。
func Test02_购买幂等(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u02")
	adopt(t, srv, tok)

	// 首次领养会赠送食品（config_points.initialItems），所以库存断言必须写成
	// 「初始值 + 3」。写死 3 会随着赠送配置变化而误报，那样的测试只是在
	// 复述当前配置，不是在校验幂等。
	_, base := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	baseInv := base["assets"].(map[string]any)["inventory"].(map[string]any)
	baseNormal := num(baseInv["food_normal"])

	var firstOrder string
	for i := 0; i < 5; i++ {
		env, data := call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
			"requestId": "r-buy-same", "foodId": "food_normal", "count": 3,
		})
		mustOK(t, env, data, "buyFood")
		order, _ := data["orderId"].(string)
		if i == 0 {
			firstOrder = order
		} else if order != firstOrder {
			t.Fatalf("幂等重放应返回首次订单号 %s，实际 %s", firstOrder, order)
		}
	}

	_, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	bal := num(sync["points"].(map[string]any)["balance"])
	if bal != 485 {
		t.Fatalf("5 次重复提交只应扣 15 分（500→485），实际 %d", bal)
	}
	inv := sync["assets"].(map[string]any)["inventory"].(map[string]any)
	want := baseNormal + 3
	if num(inv["food_normal"]) != want {
		t.Fatalf("库存应只加一次：初始 %d + 3 = %d，实际 %v",
			baseNormal, want, inv["food_normal"])
	}
}

// 验收 3：同一天签到 3 次，只有第一次发奖。
func Test03_签到只发一次(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u03")
	adopt(t, srv, tok)

	for i := 0; i < 3; i++ {
		env, data := call(t, srv, "POST", "/api/v1/checkin/claim", tok, map[string]any{
			"requestId": "r-checkin-" + itoa(i),
		})
		mustOK(t, env, data, "claimCheckIn")
	}

	_, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	bal := num(sync["points"].(map[string]any)["balance"])
	if bal != 600 {
		t.Fatalf("签到 3 次只应发一次 100（500→600），实际 %d", bal)
	}
}

// 验收 4：广告每日上限生效，超过后返回明确提示且不发奖。
func Test04_广告每日上限(t *testing.T) {
	_, srv := newApp(t, nil)
	adminTok := adminLogin(t, srv)
	publishAdLimit(t, srv, adminTok, 2, 0)

	tok, _ := login(t, srv, "u04")
	adopt(t, srv, tok)

	var lastReason string
	for i := 0; i < 3; i++ {
		env, data := call(t, srv, "POST", "/api/v1/ad/reward", tok, map[string]any{
			"requestId": "r-ad-" + itoa(i), "placement": "insufficient_points",
			"clientCompleted": true,
		})
		mustOK(t, env, data, "claimAdReward")
		if i < 2 && num(data["reward"]) != 100 {
			t.Fatalf("第 %d 次广告应发 100，实际 %v", i+1, data["reward"])
		}
		if i == 2 {
			lastReason, _ = data["rejectedReason"].(string)
			if num(data["reward"]) != 0 {
				t.Fatalf("超上限不应发奖，实际奖励 %v", data["reward"])
			}
		}
	}
	if lastReason == "" {
		t.Fatalf("超上限时应返回 rejectedReason，实际为空")
	}

	_, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	bal := num(sync["points"].(map[string]any)["balance"])
	if bal != 700 {
		t.Fatalf("两次广告应得 200（500→700），实际 %d", bal)
	}
}

// 验收 5：断网 2 小时后再登录，饱腹/快乐按 2 小时正确衰减。
func Test05_两小时衰减(t *testing.T) {
	a, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u05")
	petID := adopt(t, srv, tok)

	// 伪造 2 小时前结算
	twoHoursAgo := nowMS() - 2*3_600_000
	setPetSettle(t, a, petID, twoHoursAgo, 80, 80)

	_, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{"petId": petID})
	pet := sync["pet"].(map[string]any)
	if num(pet["satiety"]) != 60 {
		t.Fatalf("2 小时应扣饱腹 20（80→60），实际 %v", pet["satiety"])
	}
	if num(pet["happiness"]) != 70 {
		t.Fatalf("2 小时应扣快乐 10（80→70），实际 %v", pet["happiness"])
	}
}

// 验收 6：管理端改食物价格并发布，客户端下次启动拉到新价格。
func Test06_配置发布生效(t *testing.T) {
	_, srv := newApp(t, nil)
	adminTok := adminLogin(t, srv)

	tok, _ := login(t, srv, "u06")
	adopt(t, srv, tok)

	// 客户端先拿到 v1
	_, cfg1 := call(t, srv, "POST", "/api/v1/config/get", tok, map[string]any{})
	if num(cfg1["version"]) != 1 {
		t.Fatalf("初始版本应为 1，实际 %v", cfg1["version"])
	}
	if p := foodPrice(cfg1, "food_normal"); p != 5 {
		t.Fatalf("food_normal 初始价应为 5，实际 %d", p)
	}

	// 管理端改价 → 草稿 → diff → 发布
	draft := getDraft(t, srv, adminTok)
	setFoodPrice(draft, "food_normal", 8)
	saveDraft(t, srv, adminTok, draft)

	_, diff := call(t, srv, "GET", "/api/admin/config/diff", adminTok, nil)
	if num(diff["count"]) == 0 {
		t.Fatalf("发布前 diff 应至少 1 条改动")
	}

	env, pub := call(t, srv, "POST", "/api/admin/config/publish", adminTok,
		map[string]any{"note": "调价：普通狗粮 5→8"})
	mustOK(t, env, pub, "publish")
	if num(pub["version"]) != 2 {
		t.Fatalf("发布后版本应为 2，实际 %v", pub["version"])
	}

	// 客户端带旧版本号拉取，应拿到全量新配置
	_, cfg2 := call(t, srv, "POST", "/api/v1/config/get", tok, map[string]any{"version": 1})
	if unchanged, _ := cfg2["unchanged"].(bool); unchanged {
		t.Fatalf("版本不一致时必须返回全量配置，不能 unchanged")
	}
	if p := foodPrice(cfg2, "food_normal"); p != 8 {
		t.Fatalf("发布后 food_normal 价格应为 8，实际 %d", p)
	}

	// 新价格真的作用于扣费
	env3, buy := call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
		"requestId": "r-after-publish", "foodId": "food_normal", "count": 1,
	})
	mustOK(t, env3, buy, "buyFood after publish")
	if num(buy["cost"]) != 8 {
		t.Fatalf("应按新价扣 8 分，实际 %v", buy["cost"])
	}

	// 版本一致时只回 unchanged
	_, cfg3 := call(t, srv, "POST", "/api/v1/config/get", tok, map[string]any{"version": 2})
	if unchanged, _ := cfg3["unchanged"].(bool); !unchanged {
		t.Fatalf("版本一致时应返回 unchanged=true")
	}
}

// 验收 7：管理端回滚到上一版本，客户端能正确回到旧配置。
func Test07_配置回滚(t *testing.T) {
	_, srv := newApp(t, nil)
	adminTok := adminLogin(t, srv)
	tok, _ := login(t, srv, "u07")

	draft := getDraft(t, srv, adminTok)
	setFoodPrice(draft, "food_normal", 30)
	saveDraft(t, srv, adminTok, draft)
	envP, pub := call(t, srv, "POST", "/api/admin/config/publish", adminTok, map[string]any{"note": "涨价到 30"})
	mustOK(t, envP, pub, "publish")

	env, rb := call(t, srv, "POST", "/api/admin/config/rollback", adminTok,
		map[string]any{"version": 1, "note": "价格配置有误"})
	mustOK(t, env, rb, "rollback")

	_, cfg := call(t, srv, "POST", "/api/v1/config/get", tok, map[string]any{"version": 1})
	if p := foodPrice(cfg, "food_normal"); p != 5 {
		t.Fatalf("回滚到 v1 后价格应为 5，实际 %d", p)
	}
	if num(cfg["version"]) != 3 {
		t.Fatalf("回滚后版本号应继续递增为 3，实际 %v", cfg["version"])
	}
}

// 验收 8：48 小时不登录，回来状态不为空（不低于下限 1）、宠物不死。
func Test08_四十八小时不掉档(t *testing.T) {
	a, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u08")
	petID := adopt(t, srv, tok)

	// 先喂一次拿经验，验证长期离线不影响等级与经验
	call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
		"requestId": "r-b8", "foodId": "food_beef", "count": 1})
	call(t, srv, "POST", "/api/v1/pet/feed", tok, map[string]any{
		"requestId": "r-f8", "petId": petID, "foodId": "food_beef"})

	setPetSettle(t, a, petID, nowMS()-48*3_600_000, 80, 80)

	env, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{"petId": petID})
	mustOK(t, env, sync, "syncState")
	pet := sync["pet"].(map[string]any)

	// 下限 1：离线再久也停在 1，不归零（2026-10-18 产品决策）
	if got := num(pet["satiety"]); got < 1 {
		t.Fatalf("饱腹不得归零，应不低于 1，实际 %v", got)
	}
	if got := num(pet["happiness"]); got < 1 {
		t.Fatalf("快乐不得归零，应不低于 1，实际 %v", got)
	}
	if num(pet["exp"]) == 0 {
		t.Fatalf("经验不应被离线衰减清空，实际 %v", pet["exp"])
	}
	if _, ok := pet["name"]; !ok {
		t.Fatalf("宠物记录仍在，应返回完整资料")
	}
}

// 验收 9：后台看板能查到当日广告完播数与积分产出/消耗。
func Test09_看板指标(t *testing.T) {
	_, srv := newApp(t, nil)
	adminTok := adminLogin(t, srv)
	publishAdLimit(t, srv, adminTok, 5, 0)

	tok, _ := login(t, srv, "u09")
	adopt(t, srv, tok)
	call(t, srv, "POST", "/api/v1/ad/reward", tok, map[string]any{
		"requestId": "r-ad9", "placement": "insufficient_points", "clientCompleted": true})
	call(t, srv, "POST", "/api/v1/checkin/claim", tok, map[string]any{"requestId": "r-ck9"})
	call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
		"requestId": "r-b9", "foodId": "food_beef", "count": 1})

	env, data := call(t, srv, "GET", "/api/admin/dashboard?days=7", adminTok, nil)
	mustOK(t, env, data, "dashboard")
	d := data["dashboard"].(map[string]any)

	ads := d["ads"].(map[string]any)
	if num(ads["rewardedRequests"]) < 1 || num(ads["rewardedCompleted"]) < 1 {
		t.Fatalf("看板应统计到广告请求与完播，实际 %v", ads)
	}
	pts := d["points"].(map[string]any)
	if num(pts["totalEarned"]) <= 0 || num(pts["totalSpent"]) <= 0 {
		t.Fatalf("看板应统计到积分产出与消耗，实际 earned=%v spent=%v",
			pts["totalEarned"], pts["totalSpent"])
	}
	beh := d["behavior"].(map[string]any)
	if num(beh["buyCount"]) < 1 {
		t.Fatalf("看板应统计到购买次数，实际 %v", beh)
	}
	users := d["users"].(map[string]any)
	if num(users["newToday"]) < 1 || num(users["dau"]) < 1 {
		t.Fatalf("看板应统计到新增与 DAU，实际 %v", users)
	}
}

// 喂食升级：经验够了自动升级并发 100 积分。
func Test10_喂食升级发奖(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u10")
	petID := adopt(t, srv, tok)

	// food_cake：50 分 / 20 经验。L1→L2 需 100 经验，买 5 个蛋糕正好 100 经验
	// 首次领养还送了蛋糕，先记下基数，断言用相对值。
	_, base := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	baseInv := base["assets"].(map[string]any)["inventory"].(map[string]any)
	baseCake := num(baseInv["food_cake"])

	call(t, srv, "POST", "/api/v1/shop/buy-food", tok, map[string]any{
		"requestId": "r-b10", "foodId": "food_cake", "count": 5})

	var last map[string]any
	for i := 0; i < 5; i++ {
		env, data := call(t, srv, "POST", "/api/v1/pet/feed", tok, map[string]any{
			"requestId": "r-feed10-" + itoa(i), "petId": petID, "foodId": "food_cake"})
		last = mustOK(t, env, data, "feedPet")
	}

	if num(last["level"]) != 2 {
		t.Fatalf("100 经验后应升到 2 级，实际 %v", last["level"])
	}
	if num(last["levelupReward"]) != 100 {
		t.Fatalf("升 1 级应发 100 积分，实际 %v", last["levelupReward"])
	}
	// 500 - 250（买蛋糕）+ 100（升级）= 350
	if num(last["balance"]) != 350 {
		t.Fatalf("余额应为 350，实际 %v", last["balance"])
	}
	// 蛋糕库存：买了 5 个 + 初始赠送 baseCake 个，喂掉 5 个，剩 baseCake 个
	inv := last["inventory"].(map[string]any)
	if num(inv["food_cake"]) != baseCake {
		t.Fatalf("喂掉 5 个后应剩初始赠送的 %d 个，实际 %v", baseCake, inv["food_cake"])
	}
}

// 性格修改扣积分 + 冷却。
func Test11_性格修改扣费与冷却(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u11")
	petID := adopt(t, srv, tok)

	env, data := call(t, srv, "POST", "/api/v1/pet/personality", tok, map[string]any{
		"requestId": "r-per1", "petId": petID,
		"personality": map[string]any{"lively": 3, "curiosity": 3, "affection": 3, "independence": 3},
	})
	mustOK(t, env, data, "updatePersonality")
	if num(data["cost"]) != 200 {
		t.Fatalf("性格修改应扣 200，实际 %v", data["cost"])
	}
	if num(data["balance"]) != 300 {
		t.Fatalf("扣费后余额应为 300，实际 %v", data["balance"])
	}

	// 立刻再改应被冷却挡住
	env2, _ := call(t, srv, "POST", "/api/v1/pet/personality", tok, map[string]any{
		"requestId": "r-per2", "petId": petID,
		"personality": map[string]any{"lively": 5, "curiosity": 5, "affection": 5, "independence": 5},
	})
	if env2.Code != 2203 {
		t.Fatalf("冷却期内应返回 2203，实际 code=%d msg=%s", env2.Code, env2.Msg)
	}
}

// 越权：不能操作别人的宠物。
func Test12_越权访问被拒(t *testing.T) {
	_, srv := newApp(t, nil)
	tokA, _ := login(t, srv, "u12a")
	petA := adopt(t, srv, tokA)

	tokB, _ := login(t, srv, "u12b")

	env, _ := call(t, srv, "POST", "/api/v1/pet/feed", tokB, map[string]any{
		"requestId": "r-x1", "petId": petA, "foodId": "food_normal"})
	if env.Code == 0 {
		t.Fatalf("用别人的 petId 喂食不应成功")
	}

	env2, _ := call(t, srv, "POST", "/api/v1/pet/switch", tokB, map[string]any{"petId": petA})
	if env2.Code == 0 {
		t.Fatalf("不能切换到别人的宠物")
	}
}

// 互动收益按 PRD 4.3 的数值走，且受每日上限约束。
func Test13_互动收益与日上限(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u13")
	petID := adopt(t, srv, tok)

	// 多指挠动 1 次：快乐 +5，经验 +1
	env, data := call(t, srv, "POST", "/api/v1/pet/interaction", tok, map[string]any{
		"requestId": "r-i1", "petId": petID, "gesture": "SCRATCH", "count": 1})
	mustOK(t, env, data, "interaction")
	if num(data["happinessGained"]) != 5 || num(data["expGained"]) != 1 {
		t.Fatalf("多指挠动应为快乐+5 经验+1，实际 %v / %v",
			data["happinessGained"], data["expGained"])
	}

	// 单指点击 5 次：快乐 +5，经验 +1（每 5 次给 1 点）
	env2, d2 := call(t, srv, "POST", "/api/v1/pet/interaction", tok, map[string]any{
		"requestId": "r-i2", "petId": petID, "gesture": "TAP", "count": 5})
	mustOK(t, env2, d2, "interaction tap")
	if num(d2["expGained"]) != 1 {
		t.Fatalf("点击 5 次应给 1 点经验，实际 %v", d2["expGained"])
	}

	// 一次上报 100 次挠动 → 经验增量 100，但日上限 100 已用 2，只能再拿 98
	env3, d3 := call(t, srv, "POST", "/api/v1/pet/interaction", tok, map[string]any{
		"requestId": "r-i3", "petId": petID, "gesture": "SCRATCH", "count": 99})
	mustOK(t, env3, d3, "interaction cap")
	if !d3["capped"].(bool) {
		t.Fatalf("撞到日上限时应返回 capped=true，实际 %v", d3)
	}
}

// 客户端接口频控：10 秒内超过阈值拒绝（文档 6.6）。
func Test14_接口频控(t *testing.T) {
	_, srv := newApp(t, func(c *config.Config) { c.RateLimitMaxCalls = 3 })
	tok, _ := login(t, srv, "u14")
	adopt(t, srv, tok)

	var last envelope
	for i := 0; i < 6; i++ {
		last, _ = call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{})
	}
	if last.Code != 1005 {
		t.Fatalf("超过频控阈值应返回 1005，实际 code=%d", last.Code)
	}
}

// 只读运营不能发布配置。
func Test15_只读角色不能发布(t *testing.T) {
	a, srv := newApp(t, nil)
	readonlyTok := createAdmin(t, a, srv, "viewer", "viewer123", "readonly")

	// 只读角色可以查
	env, data := call(t, srv, "GET", "/api/admin/config/effective", readonlyTok, nil)
	mustOK(t, env, data, "readonly read")

	// 但不能写
	env2, _ := call(t, srv, "POST", "/api/admin/config/publish", readonlyTok, map[string]any{"note": "x"})
	if env2.Code != 1003 {
		t.Fatalf("只读角色发布应被拒(1003)，实际 code=%d", env2.Code)
	}
}

// ---------- 辅助 ----------

func itoa(i int) string {
	if i == 0 {
		return "0"
	}
	neg := i < 0
	if neg {
		i = -i
	}
	var b []byte
	for i > 0 {
		b = append([]byte{byte('0' + i%10)}, b...)
		i /= 10
	}
	if neg {
		return "-" + string(b)
	}
	return string(b)
}

func adminLogin(t *testing.T, srv *httptest.Server) string {
	t.Helper()
	env, data := call(t, srv, "POST", "/api/admin/login", "", map[string]any{
		"username": "admin", "password": "admin",
	})
	mustOK(t, env, data, "admin login")
	tok, _ := data["token"].(string)
	if tok == "" {
		t.Fatalf("管理端登录未返回 token: %v", data)
	}
	return tok
}

// publishAdLimit 调广告位的每日上限与最小间隔，走完整的草稿→发布链路。
func publishAdLimit(t *testing.T, srv *httptest.Server, adminTok string, dailyLimit, minInterval int) {
	t.Helper()
	draft := getDraft(t, srv, adminTok)
	c := draft["config"].(map[string]any)
	ads := c["ad"].([]any)
	for _, raw := range ads {
		m := raw.(map[string]any)
		if m["adType"] == "rewarded" {
			m["dailyLimit"] = dailyLimit
			m["minIntervalSec"] = minInterval
			m["enabled"] = true
		}
	}
	saveDraft(t, srv, adminTok, draft)
	env, data := call(t, srv, "POST", "/api/admin/config/publish", adminTok,
		map[string]any{"note": "测试：调整广告频控"})
	mustOK(t, env, data, "publish ad limit")
}

// createAdmin 直接建一个指定角色的管理员账号并登录。
func createAdmin(t *testing.T, a *app.App, srv *httptest.Server, user, pass, role string) string {
	t.Helper()
	hash, err := auth.HashPassword(pass)
	if err != nil {
		t.Fatalf("生成密码失败: %v", err)
	}
	_, err = a.DB.C("admin_users").InsertOne(context.Background(), bson.M{
		"_id": newTestID(), "username": user, "password_hash": hash,
		"role": role, "status": "active", "created_at": timeutil.NowMs(),
	})
	if err != nil {
		t.Fatalf("创建管理员失败: %v", err)
	}
	env, data := call(t, srv, "POST", "/api/admin/login", "", map[string]any{
		"username": user, "password": pass,
	})
	mustOK(t, env, data, "createAdmin login")
	tok, _ := data["token"].(string)
	return tok
}

func newTestID() string { return fmt.Sprintf("t%dx", time.Now().UnixNano()) }

func setPetSettle(t *testing.T, a *app.App, petID string, settleAt int64, satiety, happiness int) {
	t.Helper()
	_, err := a.DB.C("pets").UpdateOne(context.Background(), bson.M{"_id": petID},
		bson.M{"$set": bson.M{
			"last_settle_at": settleAt, "satiety": satiety, "happiness": happiness,
		}})
	if err != nil {
		t.Fatalf("改宠物结算时间失败: %v", err)
	}
}

func foodPrice(cfg map[string]any, id string) int {
	c, _ := cfg["config"].(map[string]any)
	if c == nil {
		c = cfg
	}
	foods, _ := c["food"].([]any)
	for _, f := range foods {
		m := f.(map[string]any)
		if m["id"] == id {
			return num(m["price"])
		}
	}
	return -1
}

func setFoodPrice(cfg map[string]any, id string, price int) {
	c := cfg["config"].(map[string]any)
	foods := c["food"].([]any)
	for _, f := range foods {
		m := f.(map[string]any)
		if m["id"] == id {
			m["price"] = price
		}
	}
}

func getDraft(t *testing.T, srv *httptest.Server, adminTok string) map[string]any {
	t.Helper()
	env, data := call(t, srv, "GET", "/api/admin/config/draft", adminTok, nil)
	mustOK(t, env, data, "getDraft")
	return data
}

func saveDraft(t *testing.T, srv *httptest.Server, adminTok string, draft map[string]any) {
	t.Helper()
	env, data := call(t, srv, "PUT", "/api/admin/config/draft", adminTok,
		map[string]any{"config": draft["config"]})
	mustOK(t, env, data, "saveDraft")
}

// 验收 16：首次领养按 config_points.initialItems 赠送食品。
//
// 断言刻意从「下发的配置」反推，而不是硬编码 3/2/5/2/1 ——
// 赠送清单是运营可配的，写死数字的测试只会复述当前配置，
// 真正的契约是「配置说送什么，就发什么」。
func Test16_首次领养赠送食品(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, loginData := login(t, srv, "u16")

	// 领养前背包必须是空的：赠送只在首次领养那一刻发生一次
	assets, _ := loginData["assets"].(map[string]any)
	invBefore, _ := assets["inventory"].(map[string]any)
	if len(invBefore) != 0 {
		t.Fatalf("新账号领养前背包应为空，实际 %v", invBefore)
	}

	// 从服务端下发的配置读赠送清单
	_, cfgData := call(t, srv, "POST", "/api/v1/config/get", tok, map[string]any{"version": 0})
	cfg, _ := cfgData["config"].(map[string]any)
	pts, _ := cfg["points"].(map[string]any)
	gifts, _ := pts["initialItems"].([]any)
	if len(gifts) == 0 {
		t.Fatalf("默认配置应带 initialItems，实际 points=%v", pts)
	}

	petID := adopt(t, srv, tok)

	_, sync := call(t, srv, "POST", "/api/v1/sync-state", tok, map[string]any{"petId": petID})
	invAfter := sync["assets"].(map[string]any)["inventory"].(map[string]any)

	for _, raw := range gifts {
		g, _ := raw.(map[string]any)
		id, _ := g["itemId"].(string)
		want := num(g["count"])
		if want <= 0 {
			continue
		}
		if num(invAfter[id]) != want {
			t.Fatalf("赠送项 %s 应发 %d 个，实际 %v（完整背包 %v）",
				id, want, invAfter[id], invAfter)
		}
	}
	t.Logf("首次领养赠送 %d 种食品，背包=%v", len(gifts), invAfter)
}

// 验收 17：签到状态查询接口。
//
// 覆盖三件事：
//  ① 未签时返回 claimedToday=false / canClaim=true，**且不发奖**（余额不变）
//  ② 签到后返回 claimedToday=true / canClaim=false
//  ③ 查询接口本身是幂等的 —— 连续查 5 次状态不变、余额不变
//
// 第 ① 条最关键：它是只读接口，一旦误发奖就是刷分漏洞。
func Test17_签到状态查询(t *testing.T) {
	_, srv := newApp(t, nil)
	tok, _ := login(t, srv, "u17")
	adopt(t, srv, tok)

	// ---- 签到前 ----
	env, data := call(t, srv, "POST", "/api/v1/checkin/status", tok, map[string]any{})
	mustOK(t, env, data, "checkinStatus")

	if data["claimedToday"] == true {
		t.Fatalf("刚领养不该算已签到：%v", data)
	}
	if data["canClaim"] != true {
		t.Fatalf("今天还没签，应该可以领：%v", data)
	}
	if num(data["streak"]) != 0 {
		t.Fatalf("未签到时 streak 应为 0，实际 %v", data["streak"])
	}
	if data["date"] == "" {
		t.Fatalf("应返回服务端认定的今天：%v", data)
	}
	balBefore := num(data["balance"])
	recent, _ := data["recentDates"].([]any)
	if len(recent) != 0 {
		t.Fatalf("未签到时 recentDates 应为空，实际 %v", recent)
	}

	// 只读：连查 5 次，余额不能变
	for i := 0; i < 5; i++ {
		_, d := call(t, srv, "POST", "/api/v1/checkin/status", tok, map[string]any{})
		if num(d["balance"]) != balBefore {
			t.Fatalf("只读查询改了余额：%d → %v", balBefore, d["balance"])
		}
	}

	// ---- 签到 ----
	env, claim := call(t, srv, "POST", "/api/v1/checkin/claim", tok, map[string]any{
		"requestId": "r-ck-17",
	})
	mustOK(t, env, claim, "claimCheckIn")
	if num(claim["reward"]) <= 0 {
		t.Fatalf("签到应发奖，实际 %v", claim)
	}
	if claim["alreadyClaimed"] == true {
		t.Fatalf("首次签到不该返回 alreadyClaimed：%v", claim)
	}

	// ---- 签到后 ----
	env, data2 := call(t, srv, "POST", "/api/v1/checkin/status", tok, map[string]any{})
	mustOK(t, env, data2, "checkinStatus")

	if data2["claimedToday"] != true {
		t.Fatalf("签到后 claimedToday 应为 true：%v", data2)
	}
	if data2["canClaim"] == true {
		t.Fatalf("签到后 canClaim 应为 false：%v", data2)
	}
	if num(data2["streak"]) != 1 {
		t.Fatalf("连续签到天数应为 1，实际 %v", data2["streak"])
	}
	recent2, _ := data2["recentDates"].([]any)
	if len(recent2) != 1 {
		t.Fatalf("recentDates 应有 1 天，实际 %v", recent2)
	}
	if data2["date"] != recent2[0] {
		t.Fatalf("recentDates 里的日期应等于今天：%v vs %v", data2["date"], recent2[0])
	}
	if num(data2["balance"]) != balBefore+num(claim["reward"]) {
		t.Fatalf("余额应增加奖励额：%d + %v ≠ %v",
			balBefore, claim["reward"], data2["balance"])
	}
}

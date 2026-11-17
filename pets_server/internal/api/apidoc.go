package api

import "net/http"

// 接口规格表 —— 管理端「接口文档」页的数据源。
//
// 为什么放在 Go 侧而不是写成静态 JSON：
//
//  1. 规格与路由注册在同一个包（router.go），改路由时文档就在眼前，不容易漏改。
//  2. 字段说明用 Go 常量拼装，编译期能查出拼写错误；纯 JSON 写错了要等运行时才发现。
//  3. 下发接口与其它管理端接口走同一套鉴权与频控，不额外开口子。
//
// 注意：这份表是**人写的规格声明**，不是从代码反射生成的。
// 字段与 handler 有出入时，以 handler 为准，并回来改这里。

// Field 一个请求或响应字段的说明。
type Field struct {
	Name     string `json:"name"`
	Type     string `json:"type"`
	Required bool   `json:"required,omitempty"`
	Desc     string `json:"desc,omitempty"`
}

// APISpec 单个接口的完整规格。
type APISpec struct {
	Method  string   `json:"method"`
	Path    string   `json:"path"`
	Title   string   `json:"title"`
	Group   string   `json:"group"` // 客户端 / 管理端
	Auth    string   `json:"auth"`  // 公开 / 用户 token / 管理端 token
	Roles   string   `json:"roles,omitempty"`
	Idem    bool     `json:"idem"` // 是否需要 requestId 幂等
	Desc    string   `json:"desc,omitempty"`
	Req     []Field  `json:"req,omitempty"`
	Resp    []Field  `json:"resp,omitempty"`
	Example string   `json:"example,omitempty"` // 请求体示例（JSON 字符串）
	Errs    []string `json:"errs,omitempty"`    // 这个接口可能返回的业务错误码
}

// apiSpecs 全量接口清单。顺序即页面展示顺序：客户端在前，管理端在后。
var apiSpecs = []APISpec{
	// ==================== 客户端 ====================
	{
		Method: "POST", Path: "/api/v1/login", Title: "登录", Group: "客户端", Auth: "公开",
		Desc:    "微信 wx.login 的 code 换 openid，并 upsert 账号。不存在游客态，首次进入即建号。",
		Req:     []Field{{Name: "code", Type: "string", Desc: "wx.login 返回的临时凭证（正式环境必填）"}, {Name: "devOpenid", Type: "string", Desc: "仅本地联调：DEBUG 模式且未配 WX_APPID 时可用，直接指定 openid"}, {Name: "nickname", Type: "string"}, {Name: "avatar", Type: "string"}},
		Resp:    []Field{{Name: "token", Type: "string", Desc: "JWT，后续接口放 Authorization: Bearer <token>"}, {Name: "user", Type: "object"}, {Name: "pets", Type: "array", Desc: "宠物列表（已做离线衰减结算）"}, {Name: "activePetId", Type: "string"}, {Name: "points", Type: "object"}, {Name: "assets", Type: "object", Desc: "积分余额 + 库存 + 已解锁场景"}, {Name: "configVersion", Type: "number"}, {Name: "isNewUser", Type: "boolean"}},
		Example: `{"code":"wx.login 拿到的 code"}`,
		Errs:    []string{"1001 code 无效", "1006 微信接口异常"},
	},
	{
		Method: "POST", Path: "/api/v1/config/get", Title: "拉取配置", Group: "客户端", Auth: "用户 token",
		Desc: "按版本号比对。传了 version 且与服务端一致时只回 unchanged，不重复下发全量配置。",
		Req:  []Field{{Name: "version", Type: "number", Desc: "本地缓存的配置版本，首次拉取传 0"}},
		Resp: []Field{
			{Name: "unchanged", Type: "boolean"}, {Name: "version", Type: "number"},
			{Name: "config", Type: "object", Desc: "food / scene / points / ad / flags / level / interaction 全量配置。不一致时才返回"},
			{Name: "config.scene[].layout", Type: "object", Desc: "这张背景图的实测构图数据点，客户端据此摆正交相机。" +
				"orthographicSize 正交可视半高(m) / cameraPitch 俯角(°) / cameraDistance 距离(m)；" +
				"floorFarLine 地板最远端在画面里的归一化纵向位置（0=底1=顶，只作自检基准）；" +
				"dogGroundLine 狗脚底线位置（**相机按它反推高度**，必须小于 floorFarLine）；" +
				"wanderHalfX/Z 活动区半宽半深(m) / wanderCenterX/Z 活动区中心偏移(m)；" +
				"lightEulerX/Y/Z 主光方向(°) / lightColorR/G/B 主光色 / ambientIntensity 环境光 / shadowOpacity 假阴影不透明度"},
		},
		Example: `{"version":0}`,
	},
	{
		Method: "POST", Path: "/api/v1/checkin/claim", Title: "每日签到", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "按 UTC+8 自然日判重。发奖失败会删掉签到记录，允许重试，不会出现「有签到无积分」。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true, Desc: "幂等键，重放返回首次结果"}},
		Resp:    []Field{{Name: "reward", Type: "number", Desc: "本次获得的积分"}, {Name: "balance", Type: "number"}, {Name: "streak", Type: "number", Desc: "连续签到天数"}},
		Example: `{"requestId":"cli-20261019-0001"}`,
		Errs:    []string{"2201 今天已签到", "1001 缺 requestId"},
	},
	{
		Method: "POST", Path: "/api/v1/checkin/status", Title: "签到状态查询", Group: "客户端", Auth: "用户 token",
		Desc:    "只读，**不发奖、不写库**。查今天签没签、连续几天、最近 7 天签过哪几天、今日可领多少、当前余额。",
		Req:     []Field{},
		Resp: []Field{
			{Name: "claimedToday", Type: "boolean", Desc: "今天（UTC+8）是否已签到"},
			{Name: "canClaim", Type: "boolean", Desc: "现在点签到能不能领到奖励"},
			{Name: "streak", Type: "number", Desc: "含今天的连续签到天数；今天未签时为 0"},
			{Name: "reward", Type: "number", Desc: "今日可领积分（已签则是当时实际拿到的）"},
			{Name: "recentDates", Type: "array", Desc: "最近 7 天（含今天）签过的日期 yyyy-MM-dd，升序。客户端画 7 天格子直接用这个，不要拿 streak 推算"},
			{Name: "date", Type: "string", Desc: "服务端认定的今天（UTC+8）。客户端按它算格子日期，不要用设备时间"},
			{Name: "balance", Type: "number", Desc: "当前积分余额"},
			{Name: "dailyLimit", Type: "number", Desc: "当日签到次数上限（config_points.checkinDailyLimit）"},
		},
		Example: `{}`,
	},
	{
		Method: "POST", Path: "/api/v1/ad/reward", Title: "激励视频发奖", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "服务端只认「广告位已启用 + 未超当日上限 + 距上次观看间隔达标」三个条件，通过后按配置发积分。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "adUnit", Type: "string", Required: true, Desc: "广告位 ID，须与配置一致"}, {Name: "placement", Type: "string", Desc: "场景位置标识"}, {Name: "clientCompleted", Type: "boolean", Required: true, Desc: "客户端播完回调，必须为 true 才发奖"}},
		Resp:    []Field{{Name: "reward", Type: "number"}, {Name: "balance", Type: "number"}, {Name: "dailyRewarded", Type: "number", Desc: "当日已发次数"}, {Name: "dailyCap", Type: "number"}},
		Example: `{"requestId":"cli-ad-0001","adUnit":"ad_main_video","placement":"feed_success","clientCompleted":true}`,
		Errs:    []string{"2103 广告位未启用", "2101 当日已达上限", "2102 间隔过短", "1002 广告位 ID 不匹配"},
	},
	{
		Method: "POST", Path: "/api/v1/ad/impression", Title: "插屏曝光埋点", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "只记录不发奖。用于统计广告展示量，服务端不回任何积分。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "adUnit", Type: "string", Required: true}, {Name: "placement", Type: "string"}, {Name: "completed", Type: "boolean"}},
		Resp:    []Field{{Name: "recorded", Type: "boolean"}},
		Example: `{"requestId":"cli-adimp-0001","adUnit":"ad_interstitial","placement":"app_enter","completed":true}`,
	},
	{
		Method: "POST", Path: "/api/v1/shop/buy-food", Title: "购买食物", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "扣积分与加库存是**同一条 Mongo 更新**（余额条件写进 filter），失败方向只可能是「不扣也不加」，不会出现扣了钱没到货。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "foodId", Type: "string", Required: true}, {Name: "count", Type: "number", Required: true, Desc: "数量，建议一次不超过 99"}},
		Resp:    []Field{{Name: "balance", Type: "number"}, {Name: "inventory", Type: "object", Desc: "各食物当前持有量"}},
		Example: `{"requestId":"cli-buy-0001","foodId":"food_beef","count":2}`,
		Errs:    []string{"2001 积分不足", "2002 食物不存在或已下架", "1001 缺 requestId"},
	},
	{
		Method: "POST", Path: "/api/v1/scene/unlock", Title: "解锁场景", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "按价格扣积分并加入已解锁列表。已解锁过会直接返回成功，不重复扣分。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "sceneId", Type: "string", Required: true}},
		Resp:    []Field{{Name: "balance", Type: "number"}, {Name: "scenes", Type: "array", Desc: "已解锁的场景 ID 列表"}, {Name: "alreadyOwned", Type: "boolean"}},
		Example: `{"requestId":"cli-scene-0001","sceneId":"park_day"}`,
		Errs:    []string{"2001 积分不足", "2202 已拥有", "2301 等级不足"},
	},
	{
		Method: "POST", Path: "/api/v1/scene/use", Title: "切换场景", Group: "客户端", Auth: "用户 token",
		Desc: "只能切到已解锁且启用的场景。切过去后按该场景 layout 里的实测数据点重摆相机与灯光，" +
			"所以每张背景图的构图参数都在 config_scene.layout 里下发，不在客户端代码里。",
		Req:     []Field{{Name: "sceneId", Type: "string", Required: true}},
		Resp:    []Field{{Name: "activeSceneId", Type: "string"}},
		Example: `{"sceneId":"home_day"}`,
		Errs:    []string{"1004 场景不存在", "1001 场景未解锁"},
	},
	{
		Method: "POST", Path: "/api/v1/pet/adopt", Title: "领养宠物", Group: "客户端", Auth: "用户 token",
		Desc:    "领养时要选性格四维（0~10）。**领养本身不算「修改性格」**，不进入冷却计时。",
		Req:     []Field{{Name: "petType", Type: "string", Required: true, Desc: "V1 只有 dog"}, {Name: "name", Type: "string"}, {Name: "personality", Type: "object", Required: true, Desc: "四维：lively 活泼 / curiosity 好奇 / affection 亲人 / independence 独立，各 0~10"}},
		Resp:    []Field{{Name: "pet", Type: "object", Desc: "宠物完整资料"}},
		Example: `{"petType":"dog","name":"旺财","personality":{"lively":6,"curiosity":5,"affection":7,"independence":3}}`,
		Errs:    []string{"2301 宠物数量已达上限", "2303 性格维度越界", "2202 该宠物已拥有"},
	},
	{
		Method: "POST", Path: "/api/v1/pet/feed", Title: "喂食", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "校验顺序：库存 → 扣 1 个 → 加状态 → 判升级 → 发升级奖励。状态夹在 [1, 100]，不会归零。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "petId", Type: "string", Required: true}, {Name: "foodId", Type: "string", Required: true}},
		Resp:    []Field{{Name: "pet", Type: "object", Desc: "喂食后的状态：satiety / happiness / exp / level"}, {Name: "levelUp", Type: "boolean"}, {Name: "rewardPoints", Type: "number", Desc: "升级时发放的积分，0 表示未升级"}},
		Example: `{"requestId":"cli-feed-0001","petId":"替换为真实 petId","foodId":"food_beef"}`,
		Errs:    []string{"2003 库存不足", "1004 宠物不存在", "2302 无激活宠物"},
	},
	{
		Method: "POST", Path: "/api/v1/pet/interaction", Title: "上报互动", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "**文档 13 个接口未覆盖互动经验**，但 PRD 4.3 要求互动涨快乐/经验。数值权威必须在服务端，交给客户端等于允许改包刷分，所以补这个接口：客户端只报手势与次数，收益按 InteractionConfig 计算，并受每日上限约束。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "petId", Type: "string", Required: true}, {Name: "gesture", Type: "string", Required: true, Desc: "TAP / PET / SCRATCH / SWIPE，以及背景手势 BG_SCRATCH / BG_SWIPE（手指没落在宠物身上，收益更低）"}, {Name: "count", Type: "number", Required: true, Desc: "本次手势次数"}},
		Resp:    []Field{{Name: "pet", Type: "object"}, {Name: "expGained", Type: "number"}, {Name: "happyGained", Type: "number"}, {Name: "capped", Type: "boolean", Desc: "命中每日上限"}},
		Example: `{"requestId":"cli-int-0001","petId":"替换为真实 petId","gesture":"SCRATCH","count":3}`,
		Errs:    []string{"1001 gesture 非法", "1004 宠物不存在"},
	},
	{
		Method: "POST", Path: "/api/v1/pet/switch", Title: "切换宠物", Group: "客户端", Auth: "用户 token",
		Desc:    "V1 只有一只狗，数据结构已按多宠预留。",
		Req:     []Field{{Name: "petId", Type: "string", Required: true}},
		Resp:    []Field{{Name: "activePetId", Type: "string"}, {Name: "pets", Type: "array"}},
		Example: `{"petId":"替换为真实 petId"}`,
		Errs:    []string{"1004 宠物不存在"},
	},
	{
		Method: "POST", Path: "/api/v1/pet/personality", Title: "修改性格", Group: "客户端", Auth: "用户 token", Idem: true,
		Desc:    "扣积分 + 冷却期（由积分配置里的 cooldownHours 控制）。领养时的性格选择不计入冷却。",
		Req:     []Field{{Name: "requestId", Type: "string", Required: true}, {Name: "petId", Type: "string", Required: true}, {Name: "personality", Type: "object", Required: true, Desc: "四维，各 0~10"}},
		Resp:    []Field{{Name: "pet", Type: "object"}, {Name: "balance", Type: "number"}, {Name: "nextChangeAt", Type: "number", Desc: "下次可修改的时间戳（毫秒）"}},
		Example: `{"requestId":"cli-per-0001","petId":"替换为真实 petId","personality":{"lively":4,"curiosity":6,"affection":8,"independence":2}}`,
		Errs:    []string{"2203 冷却中", "2001 积分不足", "2303 性格维度越界"},
	},
	{
		Method: "POST", Path: "/api/v1/sync-state", Title: "同步状态", Group: "客户端", Auth: "用户 token",
		Desc:    "**服务端权威状态**。按 now - lastSettleAt 整小时结算离线衰减（封顶见配置 offlineCapHours），状态夹在 [1,100]。clientState 只上报不回写，客户端不能自己改数值。",
		Req:     []Field{{Name: "petId", Type: "string"}, {Name: "clientState", Type: "object", Desc: "仅埋点，服务端不回写"}, {Name: "clientTs", Type: "number", Desc: "客户端时间戳（毫秒），仅用于统计延迟"}},
		Resp:    []Field{{Name: "pet", Type: "object", Desc: "结算后的 satiety / happiness / exp / level / satietyState / happinessState"}, {Name: "serverTs", Type: "number"}},
		Example: `{"petId":"替换为真实 petId","clientState":{"screen":"home"},"clientTs":1764000000000}`,
	},
	{
		Method: "POST", Path: "/api/v1/event/report", Title: "埋点上报", Group: "客户端", Auth: "用户 token",
		Desc:    "只写 event_log，不返回业务数据。",
		Req:     []Field{{Name: "event", Type: "string", Required: true}, {Name: "payload", Type: "object"}},
		Resp:    []Field{},
		Example: `{"event":"app_enter","payload":{"scene":"home_day"}}`,
		Errs:    []string{"1001 event 为空"},
	},

	// ==================== 管理端 ====================
	{
		Method: "POST", Path: "/api/admin/login", Title: "管理员登录", Group: "管理端", Auth: "公开",
		Desc:    "V1 单管理员模型，账号来自 ADMIN_INIT_USERNAME / ADMIN_INIT_PASSWORD。密码用 bcrypt 校验。",
		Req:     []Field{{Name: "username", Type: "string", Required: true}, {Name: "password", Type: "string", Required: true}},
		Resp:    []Field{{Name: "token", Type: "string"}, {Name: "admin", Type: "object"}, {Name: "role", Type: "string", Desc: "super / config / readonly"}},
		Example: `{"username":"admin","password":"admin"}`,
		Errs:    []string{"1001 账号或密码不对", "1002 账号被禁用"},
	},
	{
		Method: "GET", Path: "/api/admin/me", Title: "当前管理员", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "前端启动时拉一次，用于校验 token 是否还有效并显示当前角色。",
		Resp: []Field{{Name: "username", Type: "string"}, {Name: "role", Type: "string"}, {Name: "permissions", Type: "array"}},
	},
	{
		Method: "GET", Path: "/api/admin/config/effective", Title: "读生效配置", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "当前对客户端生效的版本（已发布的，不是草稿）。",
		Resp: []Field{{Name: "version", Type: "number"}, {Name: "config", Type: "object"}},
	},
	{
		Method: "GET", Path: "/api/admin/config/draft", Title: "取草稿", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "有草稿返回草稿，没有则复制一份生效态作为草稿。这是页面的编辑入口。",
		Resp: []Field{{Name: "config", Type: "object"}, {Name: "version", Type: "number", Desc: "草稿基于哪个版本"}, {Name: "updatedBy", Type: "string"}, {Name: "updatedAt", Type: "number"}},
	},
	{
		Method: "PUT", Path: "/api/admin/config/draft", Title: "存草稿", Group: "管理端", Auth: "管理端 token", Roles: "super / config",
		Desc: "保存时**立刻跑全量校验**（ValidateSnapshot），不是等到发布才拦。角色限制：config 角色改了 points 或 ad 两块会直接 403。允许未知字段（前端 UI 字段不误伤）。",
		Req:  []Field{{Name: "config", Type: "object", Required: true, Desc: "完整配置快照，不是局部字段"}},
		Resp: []Field{{Name: "saved", Type: "boolean"}},
		Errs: []string{"1003 越权改了受保护字段", "1001 校验失败（价格 / 经验 / 名称 / 路径等）"},
	},
	{
		Method: "DELETE", Path: "/api/admin/config/draft", Title: "丢弃草稿", Group: "管理端", Auth: "管理端 token", Roles: "super / config",
		Desc: "删掉草稿，下次取草稿会重新复制一份生效态。",
		Resp: []Field{{Name: "discarded", Type: "boolean"}},
	},
	{
		Method: "GET", Path: "/api/admin/config/diff", Title: "查看改动", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "逐字段对比草稿与生效态。URL 类字段（thumbUrl / sceneUrl）由服务端拼装，不参与 diff。",
		Resp: []Field{{Name: "changes", Type: "array", Desc: "每项含 path / before / after"}, {Name: "count", Type: "number"}},
	},
	{
		Method: "POST", Path: "/api/admin/config/publish", Title: "发布", Group: "管理端", Auth: "管理端 token", Roles: "super / config",
		Desc: "版本号**最后写**：写生效集合与留档成功后才递增版本。中间失败会留下「数据已改但版本没升」的状态，此时旧版本客户端仍拿旧配置、新客户端拿新配置——这个方向对用户有利。发布记录保留最近 CONFIG_RELEASE_KEEP 版。",
		Req:  []Field{{Name: "note", Type: "string", Desc: "发布说明，写入操作日志"}},
		Resp: []Field{{Name: "version", Type: "number"}, {Name: "note", Type: "string"}},
		Errs: []string{"1001 校验失败", "1004 没有可发布的草稿"},
	},
	{
		Method: "GET", Path: "/api/admin/config/releases", Title: "发布历史", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Resp: []Field{{Name: "releases", Type: "array", Desc: "版本号 / 时间 / 操作人 / 说明"}},
	},
	{
		Method: "POST", Path: "/api/admin/config/rollback", Title: "回滚", Group: "管理端", Auth: "管理端 token", Roles: "super / config",
		Desc: "把某个历史版本的内容重新发布。**版本号继续 +1 不回退**，否则会与客户端缓存的版本号撞车，导致客户端以为没变化而不拉取。",
		Req:  []Field{{Name: "version", Type: "number", Required: true, Desc: "目标版本号"}, {Name: "note", Type: "string"}},
		Resp: []Field{{Name: "version", Type: "number", Desc: "回滚后产生的新版本号"}, {Name: "hint", Type: "string"}},
		Errs: []string{"1004 版本不存在", "1001 缺 version"},
	},
	{
		Method: "GET", Path: "/api/admin/users", Title: "用户查询", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "按 openid / 昵称模糊查。V1 用户量小，走 skip/limit 分页即可。" +
			"列表项额外带活跃度指标，服务端用 3 次聚合批量算整页，查询数不随页大小线性增长。",
		Req: []Field{{Name: "keyword", Type: "string", Desc: "匹配 openid 或昵称"}, {Name: "page", Type: "number", Desc: "默认 1"}, {Name: "size", Type: "number", Desc: "默认 20"}},
		Resp: []Field{
			{Name: "list", Type: "array"},
			{Name: "total", Type: "number"}, {Name: "page", Type: "number"}, {Name: "size", Type: "number"},
			{Name: "list[].activeDays", Type: "number", Desc: "有过登录的 UTC+8 自然日数，所有「日均」指标的分母"},
			{Name: "list[].loginCount", Type: "number", Desc: "累计登录次数，含重复登录"},
			{Name: "list[].loginPerDay", Type: "number", Desc: "登录次数 ÷ 活跃天数"},
			{Name: "list[].maxOnlineMinutes", Type: "number", Desc: "单次最长在线（分钟），按登录间隔估算，单次封顶 2 小时"},
			{Name: "list[].feedPerDay", Type: "number", Desc: "日均喂养次数"},
			{Name: "list[].pointsSpentPerDay", Type: "number", Desc: "日均花费积分"},
		},
		Example: `?keyword=&page=1&size=20`,
	},
	{
		Method: "GET", Path: "/api/admin/users/{id}", Title: "用户详情", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "单个用户的积分、库存、场景解锁、宠物与流水，另附行为统计与最近 50 条喂养记录。",
		Resp: []Field{
			{Name: "user", Type: "object"}, {Name: "points", Type: "object"}, {Name: "inventory", Type: "object"},
			{Name: "scenes", Type: "array"}, {Name: "pets", Type: "array"},
			{Name: "records", Type: "array", Desc: "积分流水"},
			{Name: "stats", Type: "object", Desc: "行为统计：activeDays / loginCount / loginPerDay / totalOnlineMinutes / maxOnlineMinutes / feedCount / feedPerDay / interactCount / pointsSpentTotal / pointsSpentPerDay / petsOwned / firstActiveAt / lastActiveAt"},
			{Name: "feedRecords", Type: "array", Desc: "喂养记录：createdAt 喂养时间、foodId、foodName 食物中文名、cost 花费积分（按当前单价估算）、petId / petName"},
			{Name: "statsBasis", Type: "string", Desc: "日均口径说明，前端直接展示以免误读"},
			{Name: "statsCaveat", Type: "string", Desc: "已知偏差说明（在线时长与喂养花费均为估算）"},
		},
	},
	{
		Method: "POST", Path: "/api/admin/points/adjust", Title: "人工调整积分", Group: "管理端", Auth: "管理端 token", Roles: "仅 super",
		Desc: "**只有超管能调**。正负都可，必须填原因，会写积分流水与操作日志。",
		Req:  []Field{{Name: "userId", Type: "string", Required: true}, {Name: "delta", Type: "number", Required: true, Desc: "正数增加、负数扣减，0 视为无效"}, {Name: "note", Type: "string", Required: true, Desc: "调整原因，必填"}},
		Resp: []Field{{Name: "balance", Type: "number"}, {Name: "totalGranted", Type: "number"}, {Name: "totalSpent", Type: "number"}},
		Errs: []string{"1003 只有超管可调", "1001 原因不能为空", "2001 扣减后余额为负"},
	},
	{
		Method: "GET", Path: "/api/admin/dashboard", Title: "数据看板", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "用户 / 广告 / 积分 / 行为四组指标。**httpErrorRate 是进程内计数，重启清零**，没有落库。",
		Resp: []Field{{Name: "dashboard", Type: "object", Desc: "DAU / 新增 / 宠物数 / 签到 / 消费 / 广告展示 / 积分流通 / 通胀率"}, {Name: "httpErrorRate", Type: "number"}, {Name: "httpRequestsSinceBoot", Type: "number"}},
	},
	{
		Method: "GET", Path: "/api/admin/audit-logs", Title: "操作日志", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "发布、回滚、积分调整等所有写操作都留痕，含操作人、IP、结果。",
		Req:  []Field{{Name: "page", Type: "number"}, {Name: "size", Type: "number"}, {Name: "action", Type: "string", Desc: "按动作筛选"}},
		Resp: []Field{{Name: "list", Type: "array"}, {Name: "total", Type: "number"}},
	},
	{
		Method: "GET", Path: "/api/admin/api-doc", Title: "接口文档（本页数据源）", Group: "管理端", Auth: "管理端 token", Roles: "全部",
		Desc: "返回这份规格表本身。管理端「接口文档」页就是拿它渲染的，所以文档永远和代码同版本。" +
			"test/apidoc_test.go 会把本表与实际注册的路由逐条比对，加接口忘写文档时测试直接失败。",
		Resp: []Field{
			{Name: "specs", Type: "array", Desc: "接口规格列表"},
			{Name: "errorCodes", Type: "array", Desc: "业务错误码表"},
			{Name: "envelope", Type: "array", Desc: "响应信封字段说明"},
			{Name: "conventions", Type: "array", Desc: "全局约定（幂等、时区、货币口径等）"},
		},
	},
}

// apiErrorCodes 业务错误码表，供页面「错误码」区块展示。
var apiErrorCodes = []Field{
	{Name: "0", Type: "成功", Desc: "HTTP 200，code 为 0"},
	{Name: "1001", Type: "参数非法"},
	{Name: "1002", Type: "未登录 / token 无效", Desc: "HTTP 401"},
	{Name: "1003", Type: "越权", Desc: "HTTP 403"},
	{Name: "1004", Type: "资源不存在"},
	{Name: "1005", Type: "触发频控", Desc: "HTTP 200 + code 1005"},
	{Name: "1006", Type: "服务端内部错误", Desc: "HTTP 200 + code 1006"},
	{Name: "1007", Type: "同一 requestId 正在处理中", Desc: "幂等锁"},
	{Name: "2001", Type: "积分不足"},
	{Name: "2002", Type: "商品已下架 / 不存在"},
	{Name: "2003", Type: "库存不足"},
	{Name: "2101", Type: "广告奖励已达当日上限"},
	{Name: "2102", Type: "广告间隔过短"},
	{Name: "2103", Type: "广告位未启用"},
	{Name: "2201", Type: "已签到"},
	{Name: "2202", Type: "已拥有（场景 / 宠物）"},
	{Name: "2203", Type: "冷却中（性格修改）"},
	{Name: "2301", Type: "宠物数量已达上限"},
	{Name: "2302", Type: "无激活宠物"},
	{Name: "2303", Type: "性格维度越界"},
}

// apiDocHandler 下发接口规格。只读，任意已登录管理端角色都能看。
func (h *Handlers) apiDocHandler(w http.ResponseWriter, r *http.Request) {
	ok(w, map[string]any{
		"specs":      apiSpecs,
		"errorCodes": apiErrorCodes,
		"envelope":   []Field{{Name: "code", Type: "number", Desc: "0 表示成功，非 0 见错误码表"}, {Name: "msg", Type: "string"}, {Name: "data", Type: "object", Desc: "失败时可能为空"}},
		"conventions": []string{
			"所有响应统一信封 {code, msg, data}。业务错误 HTTP 仍是 200，靠 code 区分；未登录 401、越权 403。",
			"标注「幂等」的接口必须带 requestId。重放同一个 requestId 返回首次结果，余额不会二次变动。",
			"客户端接口的 token 放 Authorization: Bearer <token>，管理端接口同理。",
			"请求体里的字段名区分大小写，服务端用 DisallowUnknownFields，字段拼错会直接报错而不是被静默忽略。",
			"所有时间戳都是毫秒。时区口径：自然日相关（签到、每日上限、衰减结算）按 UTC+8 切分。",
			"游戏内货币单位是「积分」，V1 不接支付，没有充值入口，也没有人民币计价。",
		},
	})
}

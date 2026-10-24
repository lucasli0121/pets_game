package domain

// ============ 配置快照 ============
//
// 一份快照同时用于：草稿、发布快照、客户端全量下发、管理端 diff。
// 生效态存在各自的 config_* 集合里；发布时把快照整体写回集合，并保留一份快照。

type ConfigSnapshot struct {
	Food        []FoodItem        `json:"food" bson:"food"`
	Scene       []SceneItem       `json:"scene" bson:"scene"`
	Points      PointsConfig      `json:"points" bson:"points"`
	Ad          []AdItem          `json:"ad" bson:"ad"`
	Flags       map[string]string `json:"flags" bson:"flags"`
	Level       []LevelItem       `json:"level" bson:"level"`
	Interaction InteractionConfig `json:"interaction" bson:"interaction"`
}

// FoodItem 文档 4.3 config_food
//
// Name 直接存中文显示名，由运营在管理端随时改。
// 原先是 i18n 的 nameKey（如 food.normal），V1 只做简体中文，
// 多一层 key 只会让运营改不动文案，故改为直出中文。
type FoodItem struct {
	ID        string `json:"id" bson:"id"`
	Name      string `json:"name" bson:"name"`
	Category  string `json:"category" bson:"category"` // MAIN / SNACK / SPECIAL
	Price     int    `json:"price" bson:"price"`
	Satiety   int    `json:"satiety" bson:"satiety"`     // 饱腹增量
	Happiness int    `json:"happiness" bson:"happiness"` // 快乐增量
	Exp       int    `json:"exp" bson:"exp"`             // 经验增量
	Icon      string `json:"icon" bson:"icon"`
	Sort      int    `json:"sort" bson:"sort"`
	Enabled   bool   `json:"enabled" bson:"enabled"`
	MinLevel  int    `json:"minLevel" bson:"min_level"` // 文档 13.2：V1 不启用，默认 1
}

// SceneLayout 一张背景图的「实测构图数据点」。
//
// 为什么这些要进后台而不是留在客户端：场景形态是「2D 背景图 + 3D 宠物 + 正交相机」，
// 相机要按狗脚底线反推高度，而狗脚底线取决于这张图的地板纵深位置 ——
// 每换一张背景图这个值就变，写死在客户端 asset 里等于每加一张图都要改代码发版。
//
// 数值含义（全部是 0~1 归一化或角度，不是像素）：
//   - FloorFarLine  地板最远端（室内量踢脚线暗缝，户外量地面与远处植被的交界）在画面里的归一化纵向位置。
//     **只作构图自检基准，不参与相机计算。** 拿它当对齐目标是语义错误，
//     那等于强迫狗永远站在贴墙处。
//   - DogGroundLine 狗脚底线在画面里的归一化纵向位置。**这才是相机反推高度的目标。**
//     必须小于 FloorFarLine，否则狗会站到地板最远端之上（悬空 / 贴墙）。
//   - WanderHalfX/Z 活动区半宽 / 半深（世界单位，米）
//   - WanderCenterX/Z 活动区中心相对狗初始站位的偏移（米）。用来避开画面里的家具。
//
// 光照三项必须与背景图的实际光源匹配，否则 3D 宠物与 2D 背景会有两套影子，
// 夜间场景不压暗环境光，宠物会像贴纸浮在画面上。
type SceneLayout struct {
	OrthographicSize float64 `json:"orthographicSize" bson:"orthographic_size"` // 正交可视半高（米）
	CameraPitch      float64 `json:"cameraPitch" bson:"camera_pitch"`           // 相机俯角（度）
	CameraDistance   float64 `json:"cameraDistance" bson:"camera_distance"`     // 相机水平距离（米）

	FloorFarLine  float64 `json:"floorFarLine" bson:"floor_far_line"`
	DogGroundLine float64 `json:"dogGroundLine" bson:"dog_ground_line"`

	WanderHalfX   float64 `json:"wanderHalfX" bson:"wander_half_x"`
	WanderHalfZ   float64 `json:"wanderHalfZ" bson:"wander_half_z"`
	WanderCenterX float64 `json:"wanderCenterX" bson:"wander_center_x"`
	WanderCenterZ float64 `json:"wanderCenterZ" bson:"wander_center_z"`

	LightEulerX      float64 `json:"lightEulerX" bson:"light_euler_x"` // 主光方向（度）
	LightEulerY      float64 `json:"lightEulerY" bson:"light_euler_y"`
	LightEulerZ      float64 `json:"lightEulerZ" bson:"light_euler_z"`
	LightColorR      float64 `json:"lightColorR" bson:"light_color_r"` // 主光颜色0~1
	LightColorG      float64 `json:"lightColorG" bson:"light_color_g"`
	LightColorB      float64 `json:"lightColorB" bson:"light_color_b"`
	AmbientIntensity float64 `json:"ambientIntensity" bson:"ambient_intensity"` // 环境光强度
	ShadowOpacity    float64 `json:"shadowOpacity" bson:"shadow_opacity"`       // 脚下椭圆假阴影不透明度

	// LayoutNote 实测记录。写清楚「量的是什么东西、怎么量的」，
	// 下一张图照着量，别再靠猜。纯文本，不参与逻辑。
	LayoutNote string `json:"layoutNote" bson:"layout_note"`
}

// DefaultSceneLayout 客户端 asset 里的默认值。
// 三个字段为 0 表示「这条没量」，normalizeSceneLayout 会退回这里的值。
func DefaultSceneLayout() SceneLayout {
	return SceneLayout{
		OrthographicSize: 1.9, CameraPitch: 12, CameraDistance: 3.2,
		FloorFarLine: 0.433, DogGroundLine: 0.30,
		WanderHalfX: 0.95, WanderHalfZ: 0.70,
		LightEulerX: 42, LightEulerY: -35,
		LightColorR: 1, LightColorG: 1, LightColorB: 1,
		AmbientIntensity: 1.0, ShadowOpacity: 0.32,
	}
}

// SceneItem 文档 4.3 config_scene
//
// 资源分「远程优先、本地兜底」两套定位方式：
//   - Thumb / ScenePath 是相对 ASSET_BASE_URL 的路径，用于 CDN 直出；
//     ASSET_BASE_URL 为空时原样下发，客户端只能走包内资源。
//   - SceneKey 是打进安装包的资源 key，远程拉不到时兜底。
//
// Thumb 不设本地兜底：商店列表的小图丢了显示占位图即可，不值得为它多打一个包内资源。
//
// Layout 是这张背景图配套的实测构图数据点，见 SceneLayout 的注释。
type SceneItem struct {
	ID        string `json:"id" bson:"id"`
	Name      string `json:"name" bson:"name"` // 中文显示名，同上
	Price     int    `json:"price" bson:"price"`
	Thumb     string `json:"thumb" bson:"thumb"`
	ScenePath string `json:"scenePath" bson:"scene_path"`
	SceneKey  string `json:"sceneKey" bson:"scene_key"` // 包内资源 key，远程不可用时兜底
	Sort      int    `json:"sort" bson:"sort"`
	Enabled   bool   `json:"enabled" bson:"enabled"`
	MinLevel  int    `json:"minLevel" bson:"min_level"`

	Layout SceneLayout `json:"layout" bson:"layout"`

	// 下面两个只用于下发，不入库（bson:"-"）。
	// 由 ConfigService.ClientSnapshot 用 ASSET_BASE_URL 拼好后填入；
	// 为空串表示没配 CDN，客户端应直接走 SceneKey 加载包内资源。
	ThumbURL string `json:"thumbUrl" bson:"-"`
	SceneURL string `json:"sceneUrl" bson:"-"`
}

// InitialItem 新用户首次领养时白送的物品（2026-10-23 新增）。
//
// 为什么放在 config_points 而不是 config_food：这是「发放策略」，不是商品属性。
// 运营改赠送清单不该触发商品的配置发布；反过来下架某个商品也不该影响历史赠送。
// 两者的变更节奏不同，合在一起会让发布流程互相牵连。
type InitialItem struct {
	ItemID string `json:"itemId" bson:"item_id"`
	Count  int    `json:"count" bson:"count"`
}

// PointsConfig 文档 4.3 config_points（按 key 存在集合里，序列化时是一个对象）
type PointsConfig struct {
	Initial               int `json:"initial" bson:"initial"`                                           // 初始积分 500
	CheckinReward         int `json:"checkinReward" bson:"checkin_reward"`                              // 100
	LevelupReward         int `json:"levelupReward" bson:"levelup_reward"`                              // 100（config_level.reward_points 为 0 时兜底）
	AdReward              int `json:"adReward" bson:"ad_reward"`                                        // 100
	AdDailyLimit          int `json:"adDailyLimit" bson:"ad_daily_limit"`                               // 激励视频每日上限
	CheckinDailyLimit     int `json:"checkinDailyLimit" bson:"checkin_daily_limit"`                     // 1
	PersonalityChangeCost int `json:"personalityChangeCost" bson:"personality_change_cost"`             // 200（文档 13.3）
	PersonalityCooldownH  int `json:"personalityChangeCooldownHours" bson:"personality_cooldown_hours"` // 24

	// InitialItems 首次领养时白送的食品清单。空表示不送。
	// 与 Initial 一起发放，同一个「首次领养」事务内完成。
	InitialItems []InitialItem `json:"initialItems" bson:"initial_items"`

	// 状态阈值（文档 6.4：阈值也进配置）
	SatietyHungry      int `json:"satietyHungry" bson:"satiety_hungry"`           // 30
	SatietyExhausted   int `json:"satietyExhausted" bson:"satiety_exhausted"`     // 10
	HappinessSad       int `json:"happinessSad" bson:"happiness_sad"`             // 30
	HappinessDepressed int `json:"happinessDepressed" bson:"happiness_depressed"` // 10

	// 衰减速率（文档 6.4 写死 10 / 5；文档未要求可配，但按「数值不进代码」原则一并放进配置）
	SatietyDecayPerHour   int `json:"satietyDecayPerHour" bson:"satiety_decay_per_hour"`     // 10
	HappinessDecayPerHour int `json:"happinessDecayPerHour" bson:"happiness_decay_per_hour"` // 5
	OfflineCapHours       int `json:"offlineCapHours" bson:"offline_cap_hours"`              // 24

	// 每日总产出上限（文档 12 待确认第 1 条：通胀。0 = 不限制）
	DailyProduceCap int `json:"dailyProduceCap" bson:"daily_produce_cap"`
}

// DefaultInitialItems 新用户赠送的默认清单。
//
// 取的是客户端原型期那份背包的量级（3/2/5/2/1），这样接上后台之后
// 玩家看到的初始库存和之前一致，不会觉得「东西变少了」。
// 运营可以在管理端 A4 页改，也可以整个清空（不送）。
func DefaultInitialItems() []InitialItem {
	return []InitialItem{
		{ItemID: "food_normal", Count: 3},
		{ItemID: "food_beef", Count: 2},
		{ItemID: "food_biscuit", Count: 5},
		{ItemID: "food_chicken", Count: 2},
		{ItemID: "food_cake", Count: 1},
	}
}

// AdItem 文档 4.3 config_ad + 13.8 ② 的 ad_type / placement
type AdItem struct {
	ID             string `json:"id" bson:"id"`
	AdUnitID       string `json:"adUnitId" bson:"ad_unit_id"`
	AdType         string `json:"adType" bson:"ad_type"`      // interstitial / rewarded
	Placement      string `json:"placement" bson:"placement"` // app_open / insufficient_points / ...
	Enabled        bool   `json:"enabled" bson:"enabled"`
	MinIntervalSec int    `json:"minIntervalSec" bson:"min_interval_sec"`
	DailyLimit     int    `json:"dailyLimit" bson:"daily_limit"`
	SkipFirstDay   bool   `json:"skipFirstDay" bson:"skip_first_day"` // 仅插屏用（文档 13.8 ②）
}

// LevelItem 文档 13.8 ① config_level：完全配置化，服务端不写死公式
type LevelItem struct {
	Level        int     `json:"level" bson:"level"`
	ExpRequired  int64   `json:"expRequired" bson:"exp_required"`   // 本级升到下一级所需
	RewardPoints int     `json:"rewardPoints" bson:"reward_points"` // 升到该级后发放
	Scale        float64 `json:"scale" bson:"scale"`                // 体型缩放（客户端用）
	Enabled      bool    `json:"enabled" bson:"enabled"`
	Note         string  `json:"note" bson:"note"`
}

// InteractionConfig 互动收益（PRD 4.3）。
// 文档第 5 节的 13 个接口里没有互动经验入口，但 PRD 明确要求互动涨快乐/经验；
// 客户端上报手势、服务端按此配置计算并做每日上限，避免客户端直传数值。
type InteractionConfig struct {
	TapHappiness     int `json:"tapHappiness" bson:"tap_happiness"`         // 1
	TapExpEvery      int `json:"tapExpEvery" bson:"tap_exp_every"`          // 5 次点击 +1 经验
	PetHappiness     int `json:"petHappiness" bson:"pet_happiness"`         // 2
	PetExp           int `json:"petExp" bson:"pet_exp"`                     // 0
	ScratchHappiness int `json:"scratchHappiness" bson:"scratch_happiness"` // 5
	ScratchExp       int `json:"scratchExp" bson:"scratch_exp"`             // 1
	SwipeHappiness   int `json:"swipeHappiness" bson:"swipe_happiness"`     // 5
	SwipeExp         int `json:"swipeExp" bson:"swipe_exp"`                 // 1
	// 每日上限（防刷；0 = 不限制）
	DailyExpCap       int `json:"dailyExpCap" bson:"daily_exp_cap"`             // 100
	DailyHappinessCap int `json:"dailyHappinessCap" bson:"daily_happiness_cap"` // 300
}

// ============ 默认基线（文档 13.4 / PRD 附录 A） ============

func DefaultSnapshot() ConfigSnapshot {
	levels := DefaultLevels()
	return ConfigSnapshot{
		Food: []FoodItem{
			{ID: "food_normal", Name: "普通狗粮", Category: "MAIN", Price: 5, Satiety: 50, Happiness: 0, Exp: 5, Icon: "icon_food_normal", Sort: 1, Enabled: true, MinLevel: 1},
			{ID: "food_beef", Name: "牛肉罐头", Category: "MAIN", Price: 10, Satiety: 100, Happiness: 10, Exp: 10, Icon: "icon_food_beef", Sort: 2, Enabled: true, MinLevel: 1},
			{ID: "food_biscuit", Name: "磨牙饼干", Category: "SNACK", Price: 15, Satiety: 5, Happiness: 5, Exp: 5, Icon: "icon_food_biscuit", Sort: 3, Enabled: true, MinLevel: 1},
			{ID: "food_chicken", Name: "鸡胸肉", Category: "SNACK", Price: 20, Satiety: 5, Happiness: 10, Exp: 8, Icon: "icon_food_chicken", Sort: 4, Enabled: true, MinLevel: 1},
			{ID: "food_cake", Name: "生日蛋糕", Category: "SPECIAL", Price: 50, Satiety: 30, Happiness: 50, Exp: 20, Icon: "icon_food_cake", Sort: 5, Enabled: true, MinLevel: 1},
		},
		Scene: []SceneItem{
			{
				ID: "home_day", Name: "温馨小家（白天）", Price: 0,
				Thumb: "scenes/thumb/scene_home_day.jpg", ScenePath: "scenes/scene_home_day.jpg",
				SceneKey: "home_day", Sort: 1, Enabled: true, MinLevel: 1,
				Layout: SceneLayout{
					OrthographicSize: 1.9, CameraPitch: 12, CameraDistance: 3.2,
					// 踢脚线暗缝实测（图像y≈1085~1090，x=420/450/480/510 四列一致）
					//→ viewport = 1 - 1090/1920 = 0.433。只作自检基准。
					FloorFarLine: 0.433,
					// 狗脚底线约当地毯中部。地板可站区占画面下方 57%
					//（踢脚线 0.434、地毯上缘 0.370、底边 0），取 0.30。
					DogGroundLine: 0.30,
					WanderHalfX:   0.95, WanderHalfZ: 0.70,
					WanderCenterX: -0.30, WanderCenterZ: -0.10,
					LightEulerX: 42, LightEulerY: -35, LightEulerZ: 0,
					LightColorR: 1.0, LightColorG: 0.97, LightColorB: 0.90,
					AmbientIntensity: 1.15, ShadowOpacity: 0.32,
					LayoutNote: "室内白天。踢脚线在图像 y≈1085~1090，四列一致，暗缝特征是局部最暗点。" +
						"注意别用「最大亮度跳变」找交界，会选中地板阳光斑（y=1000）导致狗悬空。",
				},
			},
			{
				ID: "park_day", Name: "阳光公园", Price: 100,
				Thumb: "scenes/thumb/scene_park_day.jpg", ScenePath: "scenes/scene_park_day.jpg",
				SceneKey: "park_day", Sort: 2, Enabled: true, MinLevel: 1,
				Layout: SceneLayout{
					// 户外要拉远一点：草地是开阔平面，没有墙做纵深参照，
					// 狗按 1.9 画会显得过大、贴在画面上。
					OrthographicSize: 2.2, CameraPitch: 14, CameraDistance: 3.2,
					// 户外没有踢脚线。取「远处草地与灌木的交界」：
					// 图像 y≈865（x=380/430/480/530/580 五列，viewport 0.506~0.594，
					// 分散是因为草地本身有明暗斑块，取中位 0.549）。
					FloorFarLine: 0.549,
					// 站中景草地，比室内略高一点；此时距交界仍差0.25，不会贴到远处。
					DogGroundLine: 0.32,
					WanderHalfX:   1.30, WanderHalfZ: 0.90,
					WanderCenterX: 0.0, WanderCenterZ: 0.0,
					LightEulerX: 42, LightEulerY: -35, LightEulerZ: 0,
					LightColorR: 1.0, LightColorG: 1.0, LightColorB: 0.98,
					AmbientIntensity: 1.30, ShadowOpacity: 0.28,
					LayoutNote: "户外白天，草地开阔所以可视范围比室内大。" +
						"地面线取远处草地与灌木交界，不是踢脚线。" +
						"活动区可以开满：画面里没有家具需要避让。",
				},
			},
			{
				ID: "home_night", Name: "温馨小家（夜晚）", Price: 100,
				Thumb: "scenes/thumb/scene_home_night.jpg", ScenePath: "scenes/scene_home_night.jpg",
				SceneKey: "home_night", Sort: 3, Enabled: true, MinLevel: 1,
				Layout: SceneLayout{
					OrthographicSize: 1.9, CameraPitch: 12, CameraDistance: 3.2,
					// 同一张布局换夜色，踢脚线实测图像 y≈1067~1085（x=430/480/530/580），
					// 四列一致且暗缝特征明显 → viewport 0.435。
					FloorFarLine:  0.435,
					DogGroundLine: 0.30,
					WanderHalfX:   0.95, WanderHalfZ: 0.70,
					WanderCenterX: -0.30, WanderCenterZ: -0.10,
					LightEulerX: 42, LightEulerY: -35, LightEulerZ: 0,
					LightColorR: 1.0, LightColorG: 0.82, LightColorB: 0.60,
					AmbientIntensity: 0.42, ShadowOpacity: 0.22,
					LayoutNote: "室内夜晚，暖黄台灯。环境光必须压到 0.42，" +
						"否则宠物亮度远高于背景，看起来像贴纸浮在画面上。",
				},
			},
		},
		Points: PointsConfig{
			Initial:               500,
			CheckinReward:         100,
			LevelupReward:         100,
			AdReward:              100,
			AdDailyLimit:          5,
			CheckinDailyLimit:     1,
			PersonalityChangeCost: 200,
			PersonalityCooldownH:  24,
			InitialItems:          DefaultInitialItems(),
			SatietyHungry:         30,
			SatietyExhausted:      10,
			HappinessSad:          30,
			HappinessDepressed:    10,
			SatietyDecayPerHour:   10,
			HappinessDecayPerHour: 5,
			OfflineCapHours:       24,
			DailyProduceCap:       0,
		},
		Ad: []AdItem{
			{ID: "ad_rewarded_default", AdUnitID: "adunit-xxxxxxxxxxxx", AdType: AdTypeRewarded, Placement: "insufficient_points", Enabled: true, MinIntervalSec: 60, DailyLimit: 5, SkipFirstDay: false},
			{ID: "ad_interstitial_open", AdUnitID: "adunit-yyyyyyyyyyyy", AdType: AdTypeInterstitial, Placement: "app_open", Enabled: false, MinIntervalSec: 300, DailyLimit: 2, SkipFirstDay: true},
		},
		Flags: map[string]string{
			"enable_invite_reward": "false", // 文档 13.5 已删除邀请奖励
			"enable_share_reward":  "false",
			"enable_ads":           "true",
			"enable_double_pet":    "false", // V1.0 只有狗
			"enable_voice":         "false", // 文档 13.7 ① 语音 V1 不做
		},
		Level: levels,
		Interaction: InteractionConfig{
			TapHappiness:      1,
			TapExpEvery:       5,
			PetHappiness:      2,
			PetExp:            0,
			ScratchHappiness:  5,
			ScratchExp:        1,
			SwipeHappiness:    5,
			SwipeExp:          1,
			DailyExpCap:       100,
			DailyHappinessCap: 300,
		},
	}
}

// DefaultLevels 默认等级表（种子数据）。
//
// 产品决策（2026-10-18）：等级的一切数值都是配置项，服务端不做任何计算。
// 这张表只在 config_level 为空时灌一次种子，之后完全由管理端 A12 页面维护。
// 文档 13.8 ① 也要求「服务端只读表，不写死公式」。
//
// exp 逐字锁定文档 13.7 ② 的 9 行（L1→2 … L9→10），最高 10 级。
// 文档那 9 个数字无法被任何单一「×1.5 取整」规则复现：
// 338×1.5=507 对得上，但 507×1.5=760.5 按四舍五入应为 760，文档写 761；
// 761×1.5=1141.5 应为 1142，文档写 1141。原文应是人工微调过，故不做推导。
// scale 文档未给具体值，此处按每级 +0.06 列出基线，同样只是种子。
func DefaultLevels() []LevelItem {
	exp := []int64{100, 150, 225, 338, 507, 761, 1141, 1712, 2568}
	scale := []float64{1.00, 1.06, 1.12, 1.18, 1.24, 1.30, 1.36, 1.42, 1.48}
	out := make([]LevelItem, 0, len(exp))
	for i, e := range exp {
		out = append(out, LevelItem{
			Level:        i + 1,
			ExpRequired:  e,
			RewardPoints: 100,
			Scale:        scale[i],
			Enabled:      true,
		})
	}
	return out
}

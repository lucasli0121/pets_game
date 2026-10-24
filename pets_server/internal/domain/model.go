package domain

// ============ 枚举 ============

// 积分变动原因（文档 4.3 reason 枚举）
const (
	ReasonInitial     = "initial"
	ReasonCheckIn     = "checkin"
	ReasonLevelUp     = "levelup"
	ReasonAdReward    = "ad_reward"
	ReasonBuyFood     = "buy_food"
	ReasonUnlockScene = "unlock_scene"
	ReasonAdminAdjust = "admin_adjust"
	ReasonPersonality = "personality_change"
	ReasonInvite      = "invite" // 已关闭，仅保留枚举位
)

// 订单类型
const (
	OrderBuyFood     = "buy_food"
	OrderUnlockScene = "unlock_scene"
)

// 订单状态
const (
	OrderPaid = "paid"
)

// 广告类型（文档 13.8 ②）
const (
	AdTypeInterstitial = "interstitial" // 开屏插屏，不发奖
	AdTypeRewarded     = "rewarded"     // 激励视频，需服务端校验
)

// 用户状态
const (
	UserActive = "active"
	UserBanned = "banned"
)

// 管理端角色（文档 7.4）
const (
	RoleSuper    = "super"    // 超管：全部
	RoleConfig   = "config"   // 配置运营：A2-A7，不可改积分规则与广告位
	RoleReadonly = "readonly" // 只读运营
)

// 手势类型（对应 Unity 侧 GestureType，见 PRD 4.3 互动收益表）
const (
	GestureTap     = "TAP"     // 单指点击：快乐 +1，每 5 次经验 +1
	GesturePet     = "PET"     // 拖动抚摸：快乐 +2，经验 +0
	GestureScratch = "SCRATCH" // 多指挠动：快乐 +5，经验 +1
	GestureSwipe   = "SWIPE"   // 快速划动：快乐 +5，经验 +1
)

// ============ 实体 ============

type User struct {
	ID           string `bson:"_id" json:"id"`
	OpenID       string `bson:"openid" json:"openid"`
	UnionID      string `bson:"unionid,omitempty" json:"unionid,omitempty"`
	Nickname     string `bson:"nickname" json:"nickname"`
	Avatar       string `bson:"avatar" json:"avatar"`
	CreatedAt    int64  `bson:"created_at" json:"createdAt"`
	RegisteredAt int64  `bson:"registered_at" json:"registeredAt"`
	LastLoginAt  int64  `bson:"last_login_at" json:"lastLoginAt"`
	LastActiveAt int64  `bson:"last_active_at" json:"lastActiveAt"`
	Status       string `bson:"status" json:"status"`

	// 以下为管理端 A8「用户资料」统计字段，由登录流程累计（见 service.Session）。
	// 全部 omitempty：老用户没被登录流程回填过时不下发，避免把 0 当成真值。
	LoginCount     int   `bson:"login_count,omitempty" json:"loginCount,omitempty"`
	ActiveDays     int   `bson:"active_days,omitempty" json:"activeDays,omitempty"`
	TotalOnlineMs  int64 `bson:"total_online_ms,omitempty" json:"totalOnlineMs,omitempty"`
	MaxOnlineMs    int64 `bson:"max_online_ms,omitempty" json:"maxOnlineMs,omitempty"`
	LastSessionEnd int64 `bson:"last_session_end,omitempty" json:"-"`
}

// Personality 四维性格，0~10（PRD 4.6）
type Personality struct {
	Lively       int `bson:"lively" json:"lively"`             // 活泼
	Curiosity    int `bson:"curiosity" json:"curiosity"`       // 好奇
	Affection    int `bson:"affection" json:"affection"`       // 亲人
	Independence int `bson:"independence" json:"independence"` // 独立
}

// Valid 四维必须都在 0~10
func (p Personality) Valid() bool {
	for _, v := range []int{p.Lively, p.Curiosity, p.Affection, p.Independence} {
		if v < 0 || v > 10 {
			return false
		}
	}
	return true
}

type Pet struct {
	ID          string      `bson:"_id" json:"id"`
	UserID      string      `bson:"user_id" json:"userId"`
	PetType     string      `bson:"pet_type" json:"petType"` // dog / cat
	Name        string      `bson:"name" json:"name"`
	Level       int         `bson:"level" json:"level"`
	Exp         int64       `bson:"exp" json:"exp"` // 累计经验
	Satiety     int         `bson:"satiety" json:"satiety"`
	Happiness   int         `bson:"happiness" json:"happiness"`
	Personality Personality `bson:"personality" json:"personality"`

	AdoptedAt            int64 `bson:"adopted_at" json:"adoptedAt"`
	IsActive             bool  `bson:"is_active" json:"isActive"`
	LastSettleAt         int64 `bson:"last_settle_at" json:"lastSettleAt"`
	PersonalityUpdatedAt int64 `bson:"personality_updated_at" json:"personalityUpdatedAt"`
}

// PetView 下发结构：等级与级内进度由服务端按 config_level 计算，客户端不自己算。
type PetView struct {
	Pet
	ExpInLevel  int64 `json:"expInLevel"`
	ExpRequired int64 `json:"expRequired"`
	// 状态标签（PRD 4.3 阈值）
	SatietyState   string `json:"satietyState"`   // normal / hungry / exhausted
	HappinessState string `json:"happinessState"` // normal / sad / depressed
}

// Points 积分（文档 4.1 user_points）
type Points struct {
	Balance     int   `bson:"balance" json:"balance"`
	TotalEarned int   `bson:"total_earned" json:"totalEarned"`
	TotalSpent  int   `bson:"total_spent" json:"totalSpent"`
	Version     int64 `bson:"version" json:"version"`
}

// Assets 用户资产聚合根。
//
// 有意偏离文档 4.1/4.2 的「user_points / inventory / scene_unlock 三张表」：
// 生产 Mongo 为 standalone（非副本集），不支持多文档事务。购买要同时「扣积分 + 加库存」，
// 拆表只能靠两阶段补偿，存在扣了钱没加货的窗口。合并为单文档后，一次
// findOneAndUpdate 即原子完成「校验余额 + 扣减 + 加库存」，失败方向只会是「不扣也不加」。
// 逻辑视图仍是三个子对象，接口出参结构不变。
type Assets struct {
	UserID      string         `bson:"_id" json:"userId"`
	Points      Points         `bson:"points" json:"points"`
	Inventory   map[string]int `bson:"inventory" json:"inventory"` // item_id -> count
	Scenes      []string       `bson:"scenes" json:"scenes"`       // 已解锁 scene_id
	ActiveScene string         `bson:"active_scene" json:"activeScene"`
	UpdatedAt   int64          `bson:"updated_at" json:"updatedAt"`

	// 互动防刷计数：按 UTC+8 自然日重置
	DayKey         string `bson:"day_key" json:"-"`
	DayExpGained   int    `bson:"day_exp_gained" json:"-"`
	DayHappyGained int    `bson:"day_happy_gained" json:"-"`
}

// Ledger 积分流水，只增不改（文档 4.1 point_ledger）
type Ledger struct {
	ID           string `bson:"_id" json:"id"`
	UserID       string `bson:"user_id" json:"userId"`
	Delta        int    `bson:"delta" json:"delta"`
	BalanceAfter int    `bson:"balance_after" json:"balanceAfter"`
	Reason       string `bson:"reason" json:"reason"`
	RefID        string `bson:"ref_id,omitempty" json:"refId,omitempty"`
	RequestID    string `bson:"request_id,omitempty" json:"requestId,omitempty"`
	Note         string `bson:"note,omitempty" json:"note,omitempty"`
	CreatedAt    int64  `bson:"created_at" json:"createdAt"`
}

// CheckIn 签到（文档 4.2 daily_checkin），(user_id, date) 唯一索引天然防重
type CheckIn struct {
	ID        string `bson:"_id" json:"id"`
	UserID    string `bson:"user_id" json:"userId"`
	Date      string `bson:"date" json:"date"` // UTC+8 YYYY-MM-DD
	Reward    int    `bson:"reward" json:"reward"`
	Settled   bool   `bson:"settled" json:"settled"`
	CreatedAt int64  `bson:"created_at" json:"createdAt"`
}

// CheckInStatus 签到状态，随 sync-state 下发（2026-10-23 新增）。
//
// 为什么要有它：checkin/claim 是「领奖」动作，调一次就会发奖，
// 客户端不能拿它当只读查询用。而没有这个状态，界面就画不出
// 「今天签没签」和 7 天格子里哪几天打勾 —— 只能靠本地记，换设备就丢。
//
// RecentDates 给的是**最近 7 天里实际签过的日期**，不是让客户端用 streak 推算：
// streak 的定义是「从今天往前连续」，今天没签时它是 0，
// 推不出「昨天签了、今天还没签」这种最常见的状态。
type CheckInStatus struct {
	ClaimedToday bool     `json:"claimedToday"`
	Streak       int      `json:"streak"`      // 含今天的连续天数；今天未签时为 0
	Reward       int      `json:"reward"`      // 今天签到能拿多少（已签则是当时拿到的）
	RecentDates  []string `json:"recentDates"` // 最近 7 天（含今天）签过的日期，升序

	// Date 服务端认定的「今天」（UTC+8 自然日）。
	//
	// 为什么要下发：客户端也得按同一个口径算格子日期。
	// 客户端自己用 DateTime.UtcNow.AddHours(8) 算，理论上与服务端一致，
	// 但设备时间可被用户改 —— 改到明天，客户端就以为「今天没签」而多弹一次窗。
	// 服务端把这个日期明确给出来，客户端直接用，不猜。
	Date string `json:"date"`

	// CanClaim 现在点「签到」能不能拿到奖励。
	//
	// 与 ClaimedToday 分开是因为「已签」和「不能领」不是一回事：
	// 当日上限配成 0（不限制）或 >1 时，今天签过了仍可能还能再领。
	// 客户端按钮文案要按这个来，不能一律显示「今天已签到」。
	CanClaim bool `json:"canClaim"`
}

// AdEvent 广告记录（文档 4.2 ad_events）
type AdEvent struct {
	ID        string `bson:"_id" json:"id"`
	UserID    string `bson:"user_id" json:"userId"`
	AdUnit    string `bson:"ad_unit" json:"adUnit"`
	AdType    string `bson:"ad_type" json:"adType"`      // interstitial / rewarded
	Placement string `bson:"placement" json:"placement"` // app_open / insufficient_points / ...
	Completed bool   `bson:"completed" json:"completed"` // 客户端上报的完播
	Rewarded  bool   `bson:"rewarded" json:"rewarded"`   // 服务端实际发奖
	Reward    int    `bson:"reward" json:"reward"`
	LedgerID  string `bson:"ledger_id,omitempty" json:"ledgerId,omitempty"`
	CreatedAt int64  `bson:"created_at" json:"createdAt"`
}

// Order 订单（文档 4.2 orders）
type Order struct {
	ID        string `bson:"_id" json:"id"`
	OrderID   string `bson:"order_id" json:"orderId"`
	UserID    string `bson:"user_id" json:"userId"`
	Type      string `bson:"type" json:"type"`
	ItemID    string `bson:"item_id" json:"itemId"`
	Count     int    `bson:"count" json:"count"`
	Cost      int    `bson:"cost" json:"cost"`
	Status    string `bson:"status" json:"status"`
	RequestID string `bson:"request_id" json:"requestId"`
	CreatedAt int64  `bson:"created_at" json:"createdAt"`
}

// Dedup 幂等去重记录。唯一索引 (user_id, request_id) 插入成功才执行业务。
type Dedup struct {
	ID        string `bson:"_id" json:"id"`
	UserID    string `bson:"user_id" json:"userId"`
	RequestID string `bson:"request_id" json:"requestId"`
	Endpoint  string `bson:"endpoint" json:"endpoint"`
	State     string `bson:"state" json:"state"` // processing / done
	Response  string `bson:"response,omitempty" json:"response,omitempty"`
	CreatedAt int64  `bson:"created_at" json:"createdAt"`
}

// ============ 管理端实体 ============

type AdminUser struct {
	ID           string `bson:"_id" json:"id"`
	Username     string `bson:"username" json:"username"`
	PasswordHash string `bson:"password_hash" json:"-"`
	Role         string `bson:"role" json:"role"`
	Status       string `bson:"status" json:"status"`
	CreatedAt    int64  `bson:"created_at" json:"createdAt"`
	LastLoginAt  int64  `bson:"last_login_at" json:"lastLoginAt"`
}

type AuditLog struct {
	ID        string `bson:"_id" json:"id"`
	AdminID   string `bson:"admin_id" json:"adminId"`
	AdminName string `bson:"admin_name" json:"adminName"`
	Action    string `bson:"action" json:"action"`
	Target    string `bson:"target" json:"target"`
	Before    string `bson:"before,omitempty" json:"before,omitempty"`
	After     string `bson:"after,omitempty" json:"after,omitempty"`
	Note      string `bson:"note,omitempty" json:"note,omitempty"`
	IP        string `bson:"ip" json:"ip"`
	CreatedAt int64  `bson:"created_at" json:"createdAt"`
}

// ConfigDraft 草稿（文档 7.2 编辑态与发布态分离）
type ConfigDraft struct {
	ID        string `bson:"_id" json:"id"`
	Payload   string `bson:"payload" json:"payload"`
	UpdatedAt int64  `bson:"updated_at" json:"updatedAt"`
	UpdatedBy string `bson:"updated_by" json:"updatedBy"`
}

// ConfigRelease 发布快照，保留最近 N 版（文档 7.2 一键回滚）
type ConfigRelease struct {
	ID          string `bson:"_id" json:"id"`
	Version     int64  `bson:"version" json:"version"`
	Payload     string `bson:"payload" json:"payload"`
	Diff        string `bson:"diff,omitempty" json:"diff,omitempty"`
	Note        string `bson:"note" json:"note"`
	PublishedBy string `bson:"published_by" json:"publishedBy"`
	CreatedAt   int64  `bson:"created_at" json:"createdAt"`
}

// ConfigMeta 全局配置版本号（文档 4.3 config_version）
type ConfigMeta struct {
	ID        string `bson:"_id" json:"id"`
	Version   int64  `bson:"version" json:"version"`
	UpdatedAt int64  `bson:"updated_at" json:"updatedAt"`
	UpdatedBy string `bson:"updated_by" json:"updatedBy"`
	Note      string `bson:"note" json:"note"`
}

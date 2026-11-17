package service

import (
	"context"
	"log"
	"strings"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

type PetService struct {
	db      *store.DB
	cfg     *ConfigService
	assets  *AssetsService
	checkin *CheckInService
}

func NewPetService(db *store.DB, cfg *ConfigService, assets *AssetsService,
	checkin *CheckInService) *PetService {
	return &PetService{db: db, cfg: cfg, assets: assets, checkin: checkin}
}

// ---------- 登录 ----------

type LoginResult struct {
	User          *domain.User     `json:"user"`
	Pets          []domain.PetView `json:"pets"`
	ActivePetID   string           `json:"activePetId"`
	Points        domain.Points    `json:"points"`
	Assets        *domain.Assets   `json:"assets"`
	ConfigVersion int64            `json:"configVersion"`
	IsNewUser     bool             `json:"isNewUser"`
}

// Login 按 openid upsert 用户（文档 13.1）。不做游客态，首次进即建账号。
func (s *PetService) Login(ctx context.Context, openid, unionid, nickname, avatar string) (*LoginResult, error) {
	if openid == "" {
		return nil, domain.ErrParam("openid 为空")
	}
	snap, ver, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	now := timeutil.NowMs()

	var u domain.User
	err = s.db.C(store.CollUsers).FindOne(ctx, store.F("openid", openid)).Decode(&u)
	isNew := false
	switch {
	case err == mongo.ErrNoDocuments:
		isNew = true
		u = domain.User{
			ID: newID(), OpenID: openid, UnionID: unionid,
			Nickname: nickname, Avatar: avatar,
			CreatedAt: now, RegisteredAt: now, LastLoginAt: now, LastActiveAt: now,
			Status: domain.UserActive,
		}
		if _, err := s.db.C(store.CollUsers).InsertOne(ctx, u); err != nil {
			if !store.IsDuplicate(err) {
				return nil, err
			}
			// 并发注册：唯一索引挡住后重读已有账号
			if e := s.db.C(store.CollUsers).FindOne(ctx, store.F("openid", openid)).Decode(&u); e != nil {
				return nil, e
			}
			isNew = false
		}
	case err != nil:
		return nil, err
	default:
		// last_login_at / last_active_at 由 Session 统一维护，这里只更新资料字段。
		set := bson.D{}
		if nickname != "" {
			set = append(set, bson.E{Key: "nickname", Value: nickname})
		}
		if avatar != "" {
			set = append(set, bson.E{Key: "avatar", Value: avatar})
		}
		if unionid != "" {
			set = append(set, bson.E{Key: "unionid", Value: unionid})
		}
		if len(set) > 0 {
			_, _ = s.db.C(store.CollUsers).UpdateOne(ctx, store.F("_id", u.ID),
				bson.D{{Key: "$set", Value: set}})
		}
	}

	if u.Status == domain.UserBanned {
		return nil, domain.ErrForbidden("账号已被封禁")
	}

	// 活跃日记录：DAU 与留存口径取自这张表（文档 7.3）
	LogDailyActive(ctx, s.db, u.ID)

	// 登录会话统计：累计登录次数、活跃天数、结算上一次会话时长（A8 用户资料）
	Session(ctx, s.db, u.ID, now)

	pets, activeID, err := s.listPets(ctx, u.ID, snap)
	if err != nil {
		return nil, err
	}
	as, err := s.assets.Get(ctx, u.ID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}

	return &LoginResult{
		User:          &u,
		Pets:          pets,
		ActivePetID:   activeID,
		Points:        as.Points,
		Assets:        as,
		ConfigVersion: ver,
		IsNewUser:     isNew || len(pets) == 0,
	}, nil
}

func defaultSceneID(snap *domain.ConfigSnapshot) string {
	for _, sc := range snap.Scene {
		if sc.Enabled && sc.Price == 0 {
			return sc.ID
		}
	}
	if len(snap.Scene) > 0 {
		return snap.Scene[0].ID
	}
	return ""
}

// listPets 读宠物并做离线衰减结算（文档 6.4：只在 login / syncState 结算）。
func (s *PetService) listPets(ctx context.Context, userID string, snap *domain.ConfigSnapshot) ([]domain.PetView, string, error) {
	cur, err := s.db.C(store.CollPets).Find(ctx, store.F("user_id", userID),
		options.Find().SetSort(store.D("adopted_at", 1)))
	if err != nil {
		return nil, "", err
	}
	var raw []domain.Pet
	if err := cur.All(ctx, &raw); err != nil {
		return nil, "", err
	}

	curve := domain.NewLevelCurve(snap.Level)
	now := timeutil.NowMs()
	views := make([]domain.PetView, 0, len(raw))
	activeID := ""
	for i := range raw {
		p := &raw[i]
		if settled, changed := s.settle(ctx, p, snap, now); changed {
			*p = settled
		}
		views = append(views, buildPetView(*p, curve, snap.Points))
		if p.IsActive {
			activeID = p.ID
		}
	}
	if activeID == "" && len(views) > 0 {
		activeID = views[0].ID
	}
	return views, activeID, nil
}

// settle 结算离线衰减并落库。返回结算后的宠物与是否有变更。
func (s *PetService) settle(ctx context.Context, p *domain.Pet, snap *domain.ConfigSnapshot, now int64) (domain.Pet, bool) {
	res := domain.Settle(p.Satiety, p.Happiness, p.LastSettleAt, now, domain.DecayParams{
		SatietyPerHour:   snap.Points.SatietyDecayPerHour,
		HappinessPerHour: snap.Points.HappinessDecayPerHour,
		MaxHours:         snap.Points.OfflineCapHours,
		MaxState:         snap.Points.StateCap(),
	})
	if res.Hours == 0 && res.SettledAt == p.LastSettleAt {
		return *p, false
	}
	_, err := s.db.C(store.CollPets).UpdateOne(ctx, store.F("_id", p.ID),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "satiety", Value: res.Satiety},
			{Key: "happiness", Value: res.Happiness},
			{Key: "last_settle_at", Value: res.SettledAt},
		}}})
	if err != nil {
		return *p, false
	}
	out := *p
	out.Satiety, out.Happiness, out.LastSettleAt = res.Satiety, res.Happiness, res.SettledAt
	return out, true
}

func buildPetView(p domain.Pet, curve *domain.LevelCurve, pc domain.PointsConfig) domain.PetView {
	lvl, inLevel, required := curve.Resolve(p.Exp)
	p.Level = lvl
	return domain.PetView{
		Pet:            p,
		ExpInLevel:     inLevel,
		ExpRequired:    required,
		SatietyState:   domain.SatietyState(p.Satiety, pc),
		HappinessState: domain.HappinessState(p.Happiness, pc),
	}
}

// ---------- 领养 ----------

type AdoptReq struct {
	PetType     string
	Name        string
	Personality domain.Personality
}

func (s *PetService) Adopt(ctx context.Context, userID string, req AdoptReq) (*domain.PetView, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	if req.PetType == "" {
		req.PetType = "dog"
	}
	if req.PetType != "dog" && req.PetType != "cat" {
		return nil, domain.ErrParam("pet_type 必须是 dog 或 cat")
	}
	// 文档 13.5：V1.0 只有狗，猫由 enable_double_pet 开关控制
	if req.PetType == "cat" && snap.Flags["enable_double_pet"] != "true" {
		return nil, domain.ErrForbidden("V1.0 暂只提供边境牧羊犬，猫将在 V1.1 开放")
	}
	if !req.Personality.Valid() {
		return nil, domain.Err(domain.CodeBadPersonality, "性格四维必须在 0–10 之间")
	}

	existing, err := s.db.C(store.CollPets).CountDocuments(ctx, store.F("user_id", userID, "pet_type", req.PetType))
	if err != nil {
		return nil, err
	}
	if existing > 0 {
		return nil, domain.Err(domain.CodeAlreadyOwned, "已经领养过该宠物")
	}

	name := strings.TrimSpace(req.Name)
	if name == "" {
		name = "小白"
	}
	if len([]rune(name)) > 12 {
		return nil, domain.ErrParam("宠物名字最多 12 个字")
	}

	now := timeutil.NowMs()
	pet := domain.Pet{
		ID: newID(), UserID: userID, PetType: req.PetType, Name: name,
		Level: 1, Exp: 0, Satiety: 80, Happiness: 80,
		Personality: req.Personality,
		AdoptedAt:   now, IsActive: true, LastSettleAt: now,
		// 领养时的性格选择不算「修改」，不启动冷却；首次付费修改后才开始计时。
		PersonalityUpdatedAt: 0,
	}
	first := false
	total, err := s.db.C(store.CollPets).CountDocuments(ctx, store.F("user_id", userID))
	if err != nil {
		return nil, err
	}
	first = total == 0

	if _, err := s.db.C(store.CollPets).UpdateMany(ctx, store.F("user_id", userID),
		bson.D{{Key: "$set", Value: bson.D{{Key: "is_active", Value: false}}}}); err != nil {
		return nil, err
	}
	if _, err := s.db.C(store.CollPets).InsertOne(ctx, pet); err != nil {
		return nil, err
	}

	if err := s.assets.Ensure(ctx, userID, defaultSceneID(snap)); err != nil {
		return nil, err
	}
	// 文档 6.1：初始积分在领养第一只宠物时发放，走流水留痕
	if first && snap.Points.Initial > 0 {
		if _, _, err := s.assets.Grant(ctx, userID, snap.Points.Initial, domain.ReasonInitial,
			"adopt:"+pet.ID, "", "首次领养发放初始积分"); err != nil {
			return nil, err
		}
	}

	// 首次领养赠送的食品（config_points.initial_items）。
	//
	// 失败**不返回 error**：宠物上一步已经插入成功了，这里再报错会让客户端
	// 以为领养失败；玩家重试只会拿到「已经领养过该宠物」，卡死在中间。
	// 少送东西比「领养不了」轻得多，所以只记日志。
	if first && len(snap.Points.InitialItems) > 0 {
		if err := s.assets.AddItems(ctx, userID, snap.Points.InitialItems); err != nil {
			log.Printf("[pet] 首次领养赠送物品失败（宠物已创建，不阻断）：user=%s err=%v", userID, err)
		}
	}

	v := buildPetView(pet, domain.NewLevelCurve(snap.Level), snap.Points)
	return &v, nil
}

// ---------- 喂食 ----------

type FeedResult struct {
	Pet           domain.PetView  `json:"pet"`
	Satiety       int             `json:"satiety"`
	Happiness     int             `json:"happiness"`
	Exp           int64           `json:"exp"`
	Level         int             `json:"level"`
	LevelUp       bool            `json:"levelUp"`
	LevelsGained  []int           `json:"levelsGained"`
	LevelupReward int             `json:"levelupReward"`
	Balance       int             `json:"balance"`
	Inventory     map[string]int  `json:"inventory"`
	Food          domain.FoodItem `json:"food"`
}

// Feed 顺序固定：校验库存 → 扣 1 → 更新状态 → 判定升级 → 发积分（文档 6.3，不可调换）。
func (s *PetService) Feed(ctx context.Context, userID, petID, foodID string) (*FeedResult, error) {
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

	pet, err := s.getPet(ctx, userID, petID)
	if err != nil {
		return nil, err
	}
	curve := domain.NewLevelCurve(snap.Level)
	pc := snap.Points

	// 1. 校验并扣库存（原子，库存不足直接失败，不扣）
	if err := s.assets.ConsumeItem(ctx, userID, foodID); err != nil {
		return nil, err
	}

	// 先把离线衰减结清，避免喂食后的值被衰减覆盖
	settled, _ := s.settle(ctx, pet, snap, timeutil.NowMs())
	*pet = settled

	// 2. 更新状态（下限 MinStateValue，上限走配置 stateMax，状态不归零）
	stateMax := pc.StateCap()
	newSatiety := domain.ClampState(pet.Satiety+food.Satiety, stateMax)
	newHappiness := domain.ClampState(pet.Happiness+food.Happiness, stateMax)
	newExp := pet.Exp + int64(food.Exp)

	// 3. 判定升级（逐级扣减）
	oldLevel, _, _ := curve.Resolve(pet.Exp)
	newLevel, inLevel, required := curve.Resolve(newExp)

	res := &FeedResult{
		Satiety: newSatiety, Happiness: newHappiness, Exp: newExp,
		Level: newLevel, LevelUp: newLevel > oldLevel,
		LevelsGained: curve.LevelsBetween(oldLevel, newLevel),
		Food:         food,
	}

	if _, err := s.db.C(store.CollPets).UpdateOne(ctx, store.F("_id", pet.ID),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "satiety", Value: newSatiety},
			{Key: "happiness", Value: newHappiness},
			{Key: "exp", Value: newExp},
			{Key: "level", Value: newLevel},
		}}}); err != nil {
		return nil, err
	}

	// 4. 升级发奖（每升 1 级发一次）
	totalReward := 0
	for _, lv := range res.LevelsGained {
		r := curve.RewardFor(lv, pc.LevelupReward)
		if r <= 0 {
			continue
		}
		if _, _, err := s.assets.Grant(ctx, userID, r, domain.ReasonLevelUp,
			pet.ID, "", ""); err != nil {
			return nil, err
		}
		totalReward += r
	}
	res.LevelupReward = totalReward

	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	res.Balance = as.Points.Balance
	res.Inventory = as.Inventory
	LogAction(ctx, s.db, userID, pet.ID, ActionFeed, foodID)

	p := *pet
	p.Satiety, p.Happiness, p.Exp, p.Level = newSatiety, newHappiness, newExp, newLevel
	view := domain.PetView{
		Pet: p, ExpInLevel: inLevel, ExpRequired: required,
		SatietyState:   domain.SatietyState(newSatiety, pc),
		HappinessState: domain.HappinessState(newHappiness, pc),
	}
	res.Pet = view
	return res, nil
}

// ---------- 互动 ----------

type InteractResult struct {
	Pet domain.PetView `json:"pet"`
	// 字段名以接口文档（apidoc）为准：happyGained / capped。
	// 曾经写成 happinessGained / dailyCapReached，而客户端按文档读 happyGained，
	// 结果永远读到 0 —— 收益其实到账了，客户端日志却显示「快乐+0」，
	// 直接把排障方向带偏（2026-10-29 踩过一次）。
	ExpGained     int64          `json:"expGained"`
	HappyGained   int            `json:"happyGained"`
	LevelUp       bool           `json:"levelUp"`
	LevelsGained  []int          `json:"levelsGained"`
	LevelupReward int            `json:"levelupReward"`
	Balance       int            `json:"balance"`
	Capped        bool           `json:"capped"` // 命中每日上限
}

// Interact 按 PRD 4.3 的手势收益表计算，服务端算数值、客户端只报手势与次数。
//
// 为什么需要这个接口：文档第 5 节的 13 个接口里没有互动经验入口，但 PRD 明确
// 「单指点击 5 次 +1 经验、抚摸 +2 快乐、多指挠动 +5 快乐 +1 经验」。
// 若交给客户端算，等于允许改包刷经验与积分，与文档 1.3「客户端数值一律不用」冲突。
func (s *PetService) Interact(ctx context.Context, userID, petID, gesture string, count int) (*InteractResult, error) {
	if count <= 0 {
		count = 1
	}
	if count > 100 {
		return nil, domain.ErrParam("单次上报次数过多")
	}
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	ic := snap.Interaction
	var happyPer, expPer int
	switch gesture {
	case domain.GestureTap:
		happyPer, expPer = ic.TapHappiness, 0
		if ic.TapExpEvery > 0 {
			// 点击按次数累计：每满 TapExpEvery 次给 1 点经验
			expPer = count / ic.TapExpEvery
		}
	case domain.GesturePet:
		happyPer, expPer = ic.PetHappiness, ic.PetExp
	case domain.GestureScratch:
		happyPer, expPer = ic.ScratchHappiness, ic.ScratchExp
	case domain.GestureSwipe:
		happyPer, expPer = ic.SwipeHappiness, ic.SwipeExp
	case domain.GestureBgScratch:
		// 背景挠动：手指没落在宠物身上。收益压到最低，只当「有在玩」的反馈。
		happyPer, expPer = ic.BgScratchHappiness, ic.BgScratchExp
	case domain.GestureBgSwipe:
		happyPer, expPer = ic.BgSwipeHappiness, ic.BgSwipeExp
	default:
		return nil, domain.Errf(domain.CodeParam, "未知手势：%s", gesture)
	}

	addHappy := happyPer * count
	addExp := expPer
	if gesture == domain.GestureTap {
		addHappy = happyPer * count
	} else {
		addExp = expPer * count
	}

	pet, err := s.getPet(ctx, userID, petID)
	if err != nil {
		return nil, err
	}
	settled, _ := s.settle(ctx, pet, snap, timeutil.NowMs())
	*pet = settled

	// 每日上限（防刷）：实际生效量可能被压到 0
	gainedExp, gainedHappy, err := s.assets.BumpInteraction(ctx, userID,
		addExp, addHappy, ic.DailyExpCap, ic.DailyHappinessCap)
	if err != nil {
		return nil, err
	}
	capped := gainedExp < addExp || gainedHappy < addHappy

	newHappiness := domain.ClampState(pet.Happiness+gainedHappy, snap.Points.StateCap())
	newExp := pet.Exp + int64(gainedExp)
	curve := domain.NewLevelCurve(snap.Level)
	oldLevel, _, _ := curve.Resolve(pet.Exp)
	newLevel, inLevel, required := curve.Resolve(newExp)

	if _, err := s.db.C(store.CollPets).UpdateOne(ctx, store.F("_id", pet.ID),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "happiness", Value: newHappiness},
			{Key: "exp", Value: newExp},
			{Key: "level", Value: newLevel},
		}}}); err != nil {
		return nil, err
	}

	res := &InteractResult{
		ExpGained: int64(gainedExp), HappyGained: gainedHappy,
		LevelUp: newLevel > oldLevel, LevelsGained: curve.LevelsBetween(oldLevel, newLevel),
		Capped: capped,
	}
	for _, lv := range res.LevelsGained {
		r := curve.RewardFor(lv, snap.Points.LevelupReward)
		if r <= 0 {
			continue
		}
		if _, _, err := s.assets.Grant(ctx, userID, r, domain.ReasonLevelUp, pet.ID, "", ""); err != nil {
			return nil, err
		}
		res.LevelupReward += r
	}
	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	res.Balance = as.Points.Balance
	LogAction(ctx, s.db, userID, pet.ID, ActionInteract, gesture)

	p := *pet
	p.Happiness, p.Exp, p.Level = newHappiness, newExp, newLevel
	res.Pet = domain.PetView{
		Pet: p, ExpInLevel: inLevel, ExpRequired: required,
		SatietyState:   domain.SatietyState(p.Satiety, snap.Points),
		HappinessState: domain.HappinessState(newHappiness, snap.Points),
	}
	return res, nil
}

// ---------- 切换 / 性格 / 同步 ----------

func (s *PetService) Switch(ctx context.Context, userID, petID string) ([]domain.PetView, string, error) {
	if _, err := s.getPet(ctx, userID, petID); err != nil {
		return nil, "", err
	}
	if _, err := s.db.C(store.CollPets).UpdateMany(ctx, store.F("user_id", userID),
		bson.D{{Key: "$set", Value: bson.D{{Key: "is_active", Value: false}}}}); err != nil {
		return nil, "", err
	}
	if _, err := s.db.C(store.CollPets).UpdateOne(ctx, store.F("_id", petID, "user_id", userID),
		bson.D{{Key: "$set", Value: bson.D{{Key: "is_active", Value: true}}}}); err != nil {
		return nil, "", err
	}
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, "", err
	}
	LogAction(ctx, s.db, userID, petID, ActionSwitch, "")
	pets, active, err := s.listPets(ctx, userID, snap)
	return pets, active, err
}

type PersonalityResult struct {
	Pet     domain.PetView `json:"pet"`
	Cost    int            `json:"cost"`
	Balance int            `json:"balance"`
}

// UpdatePersonality 首次领养后修改性格需扣积分 + 冷却（文档 13.3）。
func (s *PetService) UpdatePersonality(ctx context.Context, userID, petID string,
	per domain.Personality) (*PersonalityResult, error) {
	if !per.Valid() {
		return nil, domain.Err(domain.CodeBadPersonality, "性格四维必须在 0–10 之间")
	}
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	pet, err := s.getPet(ctx, userID, petID)
	if err != nil {
		return nil, err
	}

	now := timeutil.NowMs()
	pc := snap.Points
	if pc.PersonalityCooldownH > 0 && pet.PersonalityUpdatedAt > 0 {
		nextAt := pet.PersonalityUpdatedAt + int64(pc.PersonalityCooldownH)*3_600_000
		if now < nextAt {
			return nil, domain.Errf(domain.CodeCooldown,
				"性格修改冷却中，%s 后可再次修改", timeutil.Format(nextAt))
		}
	}

	cost := pc.PersonalityChangeCost
	if cost > 0 {
		if _, _, err := s.assets.Spend(ctx, userID, cost, domain.ReasonPersonality,
			petID, "", "修改性格"); err != nil {
			return nil, err
		}
	}
	if _, err := s.db.C(store.CollPets).UpdateOne(ctx, store.F("_id", petID, "user_id", userID),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "personality", Value: per},
			{Key: "personality_updated_at", Value: now},
		}}}); err != nil {
		return nil, err
	}

	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	pet.Personality = per
	pet.PersonalityUpdatedAt = now
	v := buildPetView(*pet, domain.NewLevelCurve(snap.Level), pc)
	return &PersonalityResult{Pet: v, Cost: cost, Balance: as.Points.Balance}, nil
}

type SyncResult struct {
	Pet           domain.PetView        `json:"pet"`
	Points        domain.Points         `json:"points"`
	Assets        *domain.Assets        `json:"assets"`
	ConfigVersion int64                 `json:"configVersion"`
	CheckIn       *domain.CheckInStatus `json:"checkin,omitempty"`
}

// SyncState 返回服务端权威状态，并顺带结算离线衰减（文档 6.4）。
// client_state / client_ts 只记录不回写——客户端数值不作为权威。
func (s *PetService) SyncState(ctx context.Context, userID, petID string) (*SyncResult, error) {
	snap, ver, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	pet, err := s.getPet(ctx, userID, petID)
	if err != nil {
		return nil, err
	}
	settled, _ := s.settle(ctx, pet, snap, timeutil.NowMs())
	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}

	// 签到状态一并下发。客户端要画「今天签没签」和 7 天格子，
	// 而 checkin/claim 会发奖、不能当查询用。
	var checkin *domain.CheckInStatus
	if s.checkin != nil {
		if st, e := s.checkin.Status(ctx, userID); e == nil {
			checkin = st
		} else {
			// 签到状态查不到不该让整次同步失败 —— 商品和宠物数据比它重要得多
			log.Printf("[pet] 同步时读签到状态失败（不影响同步）：user=%s err=%v", userID, e)
		}
	}

	return &SyncResult{
		Pet:           buildPetView(settled, domain.NewLevelCurve(snap.Level), snap.Points),
		Points:        as.Points,
		Assets:        as,
		ConfigVersion: ver,
		CheckIn:       checkin,
	}, nil
}

// ---------- 内部 ----------

func (s *PetService) getPet(ctx context.Context, userID, petID string) (*domain.Pet, error) {
	filter := bson.M{"user_id": userID}
	if petID != "" {
		filter["_id"] = petID
	} else {
		filter["is_active"] = true
	}
	var p domain.Pet
	err := s.db.C(store.CollPets).FindOne(ctx, filter).Decode(&p)
	if err == mongo.ErrNoDocuments {
		return nil, domain.Err(domain.CodeNoActivePet, "找不到宠物，请先领养")
	}
	if err != nil {
		return nil, err
	}
	return &p, nil
}

func findFood(snap *domain.ConfigSnapshot, foodID string) (domain.FoodItem, bool) {
	for _, f := range snap.Food {
		if f.ID == foodID {
			return f, true
		}
	}
	return domain.FoodItem{}, false
}

func findScene(snap *domain.ConfigSnapshot, sceneID string) (domain.SceneItem, bool) {
	for _, sc := range snap.Scene {
		if sc.ID == sceneID {
			return sc, true
		}
	}
	return domain.SceneItem{}, false
}

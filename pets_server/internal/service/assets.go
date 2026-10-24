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

// AssetsService 积分与库存。
//
// 生产 Mongo 是 standalone，没有多文档事务（见 domain.Assets 注释）。
// 所有「必须一起成立」的变更都压到 user_assets 单文档的一次 findOneAndUpdate：
//   - 余额校验写进 filter（points.balance >= cost），一条命中即完成校验与扣减；
//   - 扣积分与加库存放在同一个 $inc 里，不会出现只扣钱没加货。
//
// 因此不需要文档 6.1 描述的「读 version → 失败重试 3 次」；version 字段仍保留并自增，
// 用于对账与后续迁移到副本集时切回真事务。
type AssetsService struct {
	db *store.DB
}

func NewAssetsService(db *store.DB) *AssetsService { return &AssetsService{db: db} }

// pointsOnly 只解码 points 子文档。比解到 bson.M 再摸类型安全——
// driver 对嵌套文档的默认解码类型依目标容器而定，用结构体就没有歧义。
type pointsOnly struct {
	Points domain.Points `bson:"points"`
}

func validItemID(id string) bool {
	if id == "" || len(id) > 64 {
		return false
	}
	if strings.ContainsAny(id, ".$") || strings.HasPrefix(id, "$") {
		return false // 防止注入到 inventory.<id> 这样的点路径
	}
	for _, r := range id {
		ok := r == '_' || r == '-' || (r >= '0' && r <= '9') || (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z')
		if !ok {
			return false
		}
	}
	return true
}

// Ensure 首次创建资产文档（领养时调用）。已存在则不动。
func (a *AssetsService) Ensure(ctx context.Context, userID, defaultScene string) error {
	now := timeutil.NowMs()
	// 不传默认场景时不要写入空字符串，否则 scenes 里会多一个空 id
	scenes := []string{}
	if defaultScene != "" {
		scenes = []string{defaultScene}
	}
	_, err := a.db.C(store.CollAssets).UpdateOne(ctx,
		store.F(store.D("_id", userID)),
		bson.D{
			{Key: "$setOnInsert", Value: bson.D{
				{Key: "points", Value: domain.Points{}},
				{Key: "inventory", Value: bson.M{}},
				{Key: "scenes", Value: scenes},
				{Key: "active_scene", Value: defaultScene},
				{Key: "updated_at", Value: now},
				{Key: "day_key", Value: timeutil.Today()},
				{Key: "day_exp_gained", Value: 0},
				{Key: "day_happy_gained", Value: 0},
			}},
		},
		options.UpdateOne().SetUpsert(true))
	return err
}

// Get 读资产。不存在时按默认值创建（幂等，兼容老数据）。
func (a *AssetsService) Get(ctx context.Context, userID, defaultScene string) (*domain.Assets, error) {
	var as domain.Assets
	err := a.db.C(store.CollAssets).FindOne(ctx, store.F(store.D("_id", userID))).Decode(&as)
	if err == mongo.ErrNoDocuments {
		if err := a.Ensure(ctx, userID, defaultScene); err != nil {
			return nil, err
		}
		err = a.db.C(store.CollAssets).FindOne(ctx, store.F(store.D("_id", userID))).Decode(&as)
	}
	if err != nil {
		return nil, err
	}
	if as.Inventory == nil {
		as.Inventory = map[string]int{}
	}
	return &as, nil
}

// Grant 加积分并写流水（文档 6.1）。delta 必须 > 0。
func (a *AssetsService) Grant(ctx context.Context, userID string, delta int, reason, refID, requestID, note string) (int, string, error) {
	if delta <= 0 {
		return 0, "", domain.ErrParam("发放积分必须大于 0")
	}
	now := timeutil.NowMs()
	var doc pointsOnly
	err := a.db.C(store.CollAssets).FindOneAndUpdate(ctx,
		store.F(store.D("_id", userID)),
		bson.D{
			{Key: "$inc", Value: bson.D{
				{Key: "points.balance", Value: delta},
				{Key: "points.total_earned", Value: delta},
				{Key: "points.version", Value: 1},
			}},
			{Key: "$set", Value: bson.D{{Key: "updated_at", Value: now}}},
			{Key: "$setOnInsert", Value: bson.D{
				{Key: "inventory", Value: bson.M{}},
				{Key: "scenes", Value: []string{}},
				{Key: "active_scene", Value: ""},
				{Key: "day_key", Value: timeutil.Today()},
			}},
		},
		options.FindOneAndUpdate().SetUpsert(true).SetReturnDocument(options.After),
	).Decode(&doc)
	if err != nil {
		return 0, "", err
	}
	bal := doc.Points.Balance
	ledgerID, err := a.writeLedger(ctx, userID, delta, bal, reason, refID, requestID, note)
	return bal, ledgerID, err
}

// Spend 扣积分（不带库存），用于解锁场景之外的纯扣减。
func (a *AssetsService) Spend(ctx context.Context, userID string, cost int, reason, refID, requestID, note string) (int, string, error) {
	if cost < 0 {
		return 0, "", domain.ErrParam("扣减积分不能为负")
	}
	if cost == 0 {
		as, err := a.Get(ctx, userID, "")
		if err != nil {
			return 0, "", err
		}
		return as.Points.Balance, "", nil
	}
	now := timeutil.NowMs()
	filter := store.F(
		store.D("_id", userID),
		store.D("points.balance", bson.M{"$gte": cost}),
	)
	update := bson.D{
		{Key: "$inc", Value: bson.D{
			{Key: "points.balance", Value: -cost},
			{Key: "points.total_spent", Value: cost},
			{Key: "points.version", Value: 1},
		}},
		{Key: "$set", Value: bson.D{{Key: "updated_at", Value: now}}},
	}
	var doc pointsOnly
	err := a.db.C(store.CollAssets).FindOneAndUpdate(ctx, filter, update,
		options.FindOneAndUpdate().SetReturnDocument(options.After)).Decode(&doc)
	if err == mongo.ErrNoDocuments {
		return 0, "", domain.ErrPointsNotEnough()
	}
	if err != nil {
		return 0, "", err
	}
	bal := doc.Points.Balance
	ledgerID, err := a.writeLedger(ctx, userID, -cost, bal, reason, refID, requestID, note)
	return bal, ledgerID, err
}

// AddItems 批量加物品，不扣积分、不发奖。用于「首次领养赠送」。
//
// 一次 $inc 多条而不是循环单条：中途失败会留下「送了一半」的状态，
// 而这里没有事务可用（生产 Mongo 是 standalone）。合成一条更新后
// 要么全送、要么全不送。
//
// 非法条目（空 id、数量 <= 0）单独跳过而不是整体失败 ——
// 运营在后台手填错一行，不该让新用户一件都领不到。
func (a *AssetsService) AddItems(ctx context.Context, userID string, items []domain.InitialItem) error {
	if len(items) == 0 {
		return nil
	}

	now := timeutil.NowMs()
	inc := bson.D{}
	sent := 0
	for _, it := range items {
		if it.Count <= 0 || !validItemID(it.ItemID) {
			log.Printf("[assets] 赠送配置里有非法条目，已跳过：itemId=%q count=%d", it.ItemID, it.Count)
			continue
		}
		inc = append(inc, bson.E{Key: "inventory." + it.ItemID, Value: it.Count})
		sent++
	}
	if sent == 0 {
		return nil
	}
	inc = append(inc, bson.E{Key: "updated_at", Value: now})

	_, err := a.db.C(store.CollAssets).UpdateOne(ctx, bson.M{"_id": userID},
		bson.D{{Key: "$inc", Value: inc}})
	if err != nil {
		return err
	}
	log.Printf("[assets] 首次领养赠送 %d 种物品给用户 %s", sent, userID)
	return nil
}

// SpendAndAddItem 原子完成「校验余额 → 扣积分 → 加库存」。
// 返回新余额与是否新建了资产文档（不存在则直接判余额不足，不做 upsert）。
func (a *AssetsService) SpendAndAddItem(ctx context.Context, userID string, cost int, itemID string, count int,
	reason, refID, requestID, note string) (int, string, error) {
	if !validItemID(itemID) {
		return 0, "", domain.Errf(domain.CodeParam, "非法商品 id：%s", itemID)
	}
	if count <= 0 {
		return 0, "", domain.ErrParam("购买数量必须大于 0")
	}
	if cost < 0 {
		return 0, "", domain.ErrParam("金额不能为负")
	}
	now := timeutil.NowMs()

	inc := bson.D{
		{Key: "points.version", Value: 1},
		{Key: "inventory." + itemID, Value: count},
	}
	filter := bson.M{"_id": userID}
	if cost > 0 {
		filter["points.balance"] = bson.M{"$gte": cost}
		inc = append(inc, bson.E{Key: "points.balance", Value: -cost})
		inc = append(inc, bson.E{Key: "points.total_spent", Value: cost})
	}
	var doc pointsOnly
	err := a.db.C(store.CollAssets).FindOneAndUpdate(ctx, filter,
		bson.D{
			{Key: "$inc", Value: inc},
			{Key: "$set", Value: bson.D{{Key: "updated_at", Value: now}}},
		},
		options.FindOneAndUpdate().SetReturnDocument(options.After)).Decode(&doc)
	if err == mongo.ErrNoDocuments {
		// 区分「余额不足」与「用户不存在」，让客户端能给出正确提示
		n, _ := a.db.C(store.CollAssets).CountDocuments(ctx, bson.M{"_id": userID})
		if n == 0 {
			return 0, "", domain.ErrNotFound("资产记录不存在")
		}
		return 0, "", domain.ErrPointsNotEnough()
	}
	if err != nil {
		return 0, "", err
	}
	bal := doc.Points.Balance
	ledgerID, err := a.writeLedger(ctx, userID, -cost, bal, reason, refID, requestID, note)
	return bal, ledgerID, err
}

// UnlockScene 原子完成「扣积分 + 解锁场景」。
func (a *AssetsService) UnlockScene(ctx context.Context, userID, sceneID string, cost int,
	refID, requestID string) (int, string, error) {
	now := timeutil.NowMs()
	inc := bson.D{{Key: "points.version", Value: 1}}
	filter := bson.M{"_id": userID, "scenes": bson.M{"$ne": sceneID}}
	if cost > 0 {
		filter["points.balance"] = bson.M{"$gte": cost}
		inc = append(inc, bson.E{Key: "points.balance", Value: -cost})
		inc = append(inc, bson.E{Key: "points.total_spent", Value: cost})
	}
	var doc pointsOnly
	err := a.db.C(store.CollAssets).FindOneAndUpdate(ctx, filter,
		bson.D{
			{Key: "$inc", Value: inc},
			{Key: "$addToSet", Value: bson.D{{Key: "scenes", Value: sceneID}}},
			{Key: "$set", Value: bson.D{{Key: "updated_at", Value: now}}},
		},
		options.FindOneAndUpdate().SetReturnDocument(options.After)).Decode(&doc)
	if err == mongo.ErrNoDocuments {
		as, e := a.Get(ctx, userID, "")
		if e != nil {
			return 0, "", e
		}
		if containsStr(as.Scenes, sceneID) {
			return 0, "", domain.Err(domain.CodeAlreadyOwned, "场景已解锁")
		}
		return 0, "", domain.ErrPointsNotEnough()
	}
	if err != nil {
		return 0, "", err
	}
	bal := doc.Points.Balance
	ledgerID, err := a.writeLedger(ctx, userID, -cost, bal, domain.ReasonUnlockScene, refID, requestID, "unlock "+sceneID)
	return bal, ledgerID, err
}

// ConsumeItem 原子扣库存（喂食）。库存不足返回 CodeNoInventory。
func (a *AssetsService) ConsumeItem(ctx context.Context, userID, itemID string) error {
	if !validItemID(itemID) {
		return domain.Errf(domain.CodeParam, "非法商品 id：%s", itemID)
	}
	res, err := a.db.C(store.CollAssets).UpdateOne(ctx,
		store.F(
			store.D("_id", userID),
			store.D("inventory."+itemID, bson.M{"$gte": 1}),
		),
		bson.D{
			{Key: "$inc", Value: bson.D{{Key: "inventory." + itemID, Value: -1}}},
			{Key: "$set", Value: bson.D{{Key: "updated_at", Value: timeutil.NowMs()}}},
		})
	if err != nil {
		return err
	}
	if res.MatchedCount == 0 {
		return domain.ErrNoInventory()
	}
	return nil
}

// SetActiveScene 切换当前场景（需已解锁）。
func (a *AssetsService) SetActiveScene(ctx context.Context, userID, sceneID string) error {
	res, err := a.db.C(store.CollAssets).UpdateOne(ctx,
		store.F(store.D("_id", userID), store.D("scenes", sceneID)),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "active_scene", Value: sceneID},
			{Key: "updated_at", Value: timeutil.NowMs()},
		}}})
	if err != nil {
		return err
	}
	if res.MatchedCount == 0 {
		return domain.Err(domain.CodeNotFound, "场景未解锁")
	}
	return nil
}

// BumpInteraction 记录互动收益并做每日上限限制（原子）。
// 返回实际生效的经验与快乐增量（可能因为日上限被压到 0）。
func (a *AssetsService) BumpInteraction(ctx context.Context, userID string, exp, happy int, capExp, capHappy int) (int, int, error) {
	day := timeutil.Today()
	if exp == 0 && happy == 0 {
		return 0, 0, nil
	}

	// 先确保 day_key 是今天（跨日重置计数）
	_, err := a.db.C(store.CollAssets).UpdateOne(ctx,
		store.F(store.D("_id", userID), store.D("day_key", bson.M{"$ne": day})),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "day_key", Value: day},
			{Key: "day_exp_gained", Value: 0},
			{Key: "day_happy_gained", Value: 0},
		}}})
	if err != nil && err != mongo.ErrNoDocuments {
		return 0, 0, err
	}

	filter := bson.M{"_id": userID, "day_key": day}
	conds := bson.A{}
	if exp > 0 && capExp > 0 {
		conds = append(conds, bson.M{"day_exp_gained": bson.M{"$lte": capExp - exp}})
	}
	if happy > 0 && capHappy > 0 {
		conds = append(conds, bson.M{"day_happy_gained": bson.M{"$lte": capHappy - happy}})
	}
	if len(conds) > 0 {
		filter["$and"] = conds
	}

	inc := bson.D{}
	if exp != 0 {
		inc = append(inc, bson.E{Key: "day_exp_gained", Value: exp})
	}
	if happy != 0 {
		inc = append(inc, bson.E{Key: "day_happy_gained", Value: happy})
	}
	if len(inc) == 0 {
		return 0, 0, nil
	}

	res, err := a.db.C(store.CollAssets).UpdateOne(ctx, filter,
		bson.D{
			{Key: "$inc", Value: inc},
			{Key: "$set", Value: bson.D{{Key: "updated_at", Value: timeutil.NowMs()}}},
		})
	if err != nil {
		return 0, 0, err
	}
	if res.MatchedCount == 0 {
		// 上限已到，本次收益全部或部分不生效：降级为只加快乐 / 只加经验
		return a.bumpPartial(ctx, userID, day, exp, happy, capExp, capHappy)
	}
	return exp, happy, nil
}

// bumpPartial 当经验与快乐任一撞到日上限时，退化为只加未超限的那一项。
func (a *AssetsService) bumpPartial(ctx context.Context, userID, day string, exp, happy, capExp, capHappy int) (int, int, error) {
	var as domain.Assets
	if err := a.db.C(store.CollAssets).FindOne(ctx, store.F(store.D("_id", userID))).Decode(&as); err != nil {
		return 0, 0, err
	}
	gainedExp, gainedHappy := 0, 0
	inc := bson.D{}
	if exp > 0 && (capExp <= 0 || as.DayExpGained+exp <= capExp) {
		inc = append(inc, bson.E{Key: "day_exp_gained", Value: exp})
		gainedExp = exp
	}
	if happy > 0 && (capHappy <= 0 || as.DayHappyGained+happy <= capHappy) {
		inc = append(inc, bson.E{Key: "day_happy_gained", Value: happy})
		gainedHappy = happy
	}
	if len(inc) == 0 {
		return 0, 0, nil
	}
	_, err := a.db.C(store.CollAssets).UpdateOne(ctx, store.F(store.D("_id", userID)),
		bson.D{
			{Key: "$inc", Value: inc},
			{Key: "$set", Value: bson.D{{Key: "day_key", Value: day}}},
		})
	return gainedExp, gainedHappy, err
}

func (a *AssetsService) writeLedger(ctx context.Context, userID string, delta, balanceAfter int,
	reason, refID, requestID, note string) (string, error) {
	l := domain.Ledger{
		ID: newID(), UserID: userID, Delta: delta, BalanceAfter: balanceAfter,
		Reason: reason, RefID: refID, RequestID: requestID, Note: note,
		CreatedAt: timeutil.NowMs(),
	}
	if _, err := a.db.C(store.CollLedger).InsertOne(ctx, l); err != nil {
		return "", err
	}
	return l.ID, nil
}

// ---------- 只读查询 ----------

func (a *AssetsService) Ledger(ctx context.Context, userID string, limit int64) ([]domain.Ledger, error) {
	if limit <= 0 || limit > 200 {
		limit = 50
	}
	cur, err := a.db.C(store.CollLedger).Find(ctx, store.F(store.D("user_id", userID)),
		options.Find().SetSort(store.D("created_at", -1)).SetLimit(limit))
	if err != nil {
		return nil, err
	}
	var out []domain.Ledger
	return out, cur.All(ctx, &out)
}

func containsStr(ss []string, s string) bool {
	for _, v := range ss {
		if v == s {
			return true
		}
	}
	return false
}

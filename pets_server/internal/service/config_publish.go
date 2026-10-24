package service

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

// ---------- 校验（文档 7.2 数值校验） ----------

func ValidateSnapshot(s *domain.ConfigSnapshot) error {
	if len(s.Food) == 0 {
		return domain.ErrParam("至少需要一个食物配置")
	}
	ids := map[string]bool{}
	categories := map[string]bool{"MAIN": true, "SNACK": true, "SPECIAL": true}
	enabledFood := 0
	for i := range s.Food {
		f := &s.Food[i]
		if strings.TrimSpace(f.ID) == "" {
			return domain.ErrParam("食物 id 不能为空")
		}
		if ids[f.ID] {
			return domain.Errf(domain.CodeParam, "食物 id 重复：%s", f.ID)
		}
		ids[f.ID] = true
		if strings.TrimSpace(f.Name) == "" {
			return domain.Errf(domain.CodeParam, "%s 的名称不能为空", f.ID)
		}
		if f.Price < 0 {
			return domain.Errf(domain.CodeParam, "%s 价格不能为负", f.ID)
		}
		if f.Satiety < 0 || f.Satiety > 100 {
			return domain.Errf(domain.CodeParam, "%s 饱腹增量须在 0–100", f.ID)
		}
		if f.Happiness < 0 || f.Happiness > 100 {
			return domain.Errf(domain.CodeParam, "%s 快乐增量须在 0–100", f.ID)
		}
		if f.Exp < 0 || f.Exp > 1000 {
			return domain.Errf(domain.CodeParam, "%s 经验增量须在 0–1000", f.ID)
		}
		if !categories[f.Category] {
			return domain.Errf(domain.CodeParam, "%s 分类必须是 MAIN/SNACK/SPECIAL", f.ID)
		}
		if f.MinLevel < 1 {
			f.MinLevel = 1
		}
		if f.Enabled {
			enabledFood++
		}
	}
	if enabledFood == 0 {
		return domain.ErrParam("至少要有一个上架的食物")
	}

	// 新用户赠送清单。必须指向上面那批食物里的 id ——
	// 否则运营删了某个商品、赠送清单没同步改，新用户就领到一件
	// 客户端配置里根本不存在的物品（背包里有个永远显示不出来的条目）。
	for i, it := range s.Points.InitialItems {
		if strings.TrimSpace(it.ItemID) == "" {
			return domain.Errf(domain.CodeParam, "新用户赠送第 %d 行：物品 id 不能为空", i+1)
		}
		if !ids[it.ItemID] {
			return domain.Errf(domain.CodeParam,
				"新用户赠送第 %d 行：商品 %s 不在食物列表里", i+1, it.ItemID)
		}
		if it.Count <= 0 || it.Count > 999 {
			return domain.Errf(domain.CodeParam, "新用户赠送第 %d 行：数量须在 1–999", i+1)
		}
	}

	if len(s.Scene) == 0 {
		return domain.ErrParam("至少需要一个场景配置")
	}
	ids = map[string]bool{}
	enabledScene, freeScene := 0, 0
	for i := range s.Scene {
		sc := &s.Scene[i]
		if strings.TrimSpace(sc.ID) == "" {
			return domain.ErrParam("场景 id 不能为空")
		}
		if ids[sc.ID] {
			return domain.Errf(domain.CodeParam, "场景 id 重复：%s", sc.ID)
		}
		ids[sc.ID] = true
		if strings.TrimSpace(sc.Name) == "" {
			return domain.Errf(domain.CodeParam, "%s 的名称不能为空", sc.ID)
		}
		if err := checkAssetPath(sc.Thumb); err != nil {
			return domain.Errf(domain.CodeParam, "%s 的缩略图路径不合法：%v", sc.ID, err)
		}
		if err := checkAssetPath(sc.ScenePath); err != nil {
			return domain.Errf(domain.CodeParam, "%s 的场景图路径不合法：%v", sc.ID, err)
		}
		if strings.TrimSpace(sc.ScenePath) == "" && strings.TrimSpace(sc.SceneKey) == "" {
			return domain.Errf(domain.CodeParam,
				"%s 的场景图路径与场景资源 key 不能同时为空，否则客户端无从加载背景", sc.ID)
		}
		if sc.Price < 0 {
			return domain.Errf(domain.CodeParam, "%s 价格不能为负", sc.ID)
		}
		if sc.MinLevel < 1 {
			sc.MinLevel = 1
		}
		normalizeSceneLayout(&sc.Layout)
		if err := validateSceneLayout(sc.ID, &sc.Layout); err != nil {
			return err
		}
		if sc.Enabled {
			enabledScene++
			if sc.Price == 0 {
				freeScene++
			}
		}
	}
	if enabledScene == 0 {
		return domain.ErrParam("至少要有一个启用的场景")
	}
	if freeScene == 0 {
		return domain.ErrParam("必须保留至少一个免费场景，否则新用户无处可去")
	}

	// 等级表：必须从 1 起连续，经验递增且大于 0（文档 13.8 ①）
	if len(s.Level) == 0 {
		return domain.ErrParam("等级配置不能为空")
	}
	sortLevels(s.Level)
	for i := range s.Level {
		lv := s.Level[i]
		if lv.Level != i+1 {
			return domain.Errf(domain.CodeParam, "等级必须从 1 起连续，第 %d 行是 %d", i+1, lv.Level)
		}
		if lv.ExpRequired <= 0 {
			return domain.Errf(domain.CodeParam, "L%d 的升级经验必须大于 0", lv.Level)
		}
		if i > 0 && lv.ExpRequired <= s.Level[i-1].ExpRequired {
			return domain.Errf(domain.CodeParam, "L%d 的升级经验必须大于 L%d", lv.Level, s.Level[i-1].Level)
		}
		if lv.RewardPoints < 0 {
			return domain.Errf(domain.CodeParam, "L%d 的升级奖励不能为负", lv.Level)
		}
	}

	p := s.Points
	if p.Initial < 0 || p.CheckinReward < 0 || p.LevelupReward < 0 || p.AdReward < 0 {
		return domain.ErrParam("积分数值不能为负")
	}
	if p.AdReward > 500 {
		return domain.ErrParam("广告奖励不能超过 500")
	}
	if p.Initial > 100000 {
		return domain.ErrParam("初始积分不能超过 100000")
	}
	if p.AdDailyLimit < 0 || p.CheckinDailyLimit < 0 {
		return domain.ErrParam("每日上限不能为负")
	}
	if p.PersonalityChangeCost < 0 {
		return domain.ErrParam("性格修改费用不能为负")
	}
	if p.SatietyHungry <= p.SatietyExhausted {
		return domain.ErrParam("饱腹阈值须满足：饥饿阈值 > 疲惫阈值")
	}
	if p.HappinessSad <= p.HappinessDepressed {
		return domain.ErrParam("快乐阈值须满足：难过阈值 > 沮丧阈值")
	}
	if p.DailyProduceCap < 0 {
		return domain.ErrParam("每日总产出上限不能为负")
	}
	if p.SatietyDecayPerHour < 0 || p.HappinessDecayPerHour < 0 {
		return domain.ErrParam("状态衰减速率不能为负")
	}
	if p.OfflineCapHours <= 0 {
		s.Points.OfflineCapHours = 24
	}
	if p.OfflineCapHours > 168 {
		return domain.ErrParam("离线衰减封顶小时数不宜超过 168（7 天）")
	}

	for i := range s.Ad {
		ad := &s.Ad[i]
		if ad.AdType != domain.AdTypeInterstitial && ad.AdType != domain.AdTypeRewarded {
			return domain.Errf(domain.CodeParam, "%s 的 adType 必须是 interstitial/rewarded", ad.ID)
		}
		if ad.MinIntervalSec < 0 || ad.DailyLimit < 0 {
			return domain.Errf(domain.CodeParam, "%s 的频控参数不能为负", ad.ID)
		}
	}

	ic := s.Interaction
	for name, v := range map[string]int{
		"tapHappiness": ic.TapHappiness, "petHappiness": ic.PetHappiness,
		"scratchHappiness": ic.ScratchHappiness, "swipeHappiness": ic.SwipeHappiness,
		"petExp": ic.PetExp, "scratchExp": ic.ScratchExp, "swipeExp": ic.SwipeExp,
		"dailyExpCap": ic.DailyExpCap, "dailyHappinessCap": ic.DailyHappinessCap,
	} {
		if v < 0 {
			return domain.Errf(domain.CodeParam, "互动配置 %s 不能为负", name)
		}
	}
	if ic.TapExpEvery <= 0 {
		return domain.ErrParam("tapExpEvery 必须大于 0")
	}
	return nil
}

// validateSceneLayout 校验一张背景图的实测构图数据点。
//
// 这些值直接决定客户端相机的位置与狗的活动范围，
// 配错的后果不是「看起来不对」而是画面直接废掉（狗跑到画面外、陷进地板、悬空）。
// 因此在发布侧拦住，而不是让客户端拿到后自行崩溃。
func validateSceneLayout(sceneID string, l *domain.SceneLayout) error {
	bad := func(msg string) error {
		return domain.Errf(domain.CodeParam, "%s 的构图参数%s", sceneID, msg)
	}
	if l.OrthographicSize < 0.5 || l.OrthographicSize > 10 {
		return bad(fmt.Sprintf("正交可视半高 %.3f 超出 0.5~10 m", l.OrthographicSize))
	}
	if l.CameraPitch < 0 || l.CameraPitch > 45 {
		return bad(fmt.Sprintf("相机俯角 %.1f° 超出 0~45°", l.CameraPitch))
	}
	if l.CameraDistance < 0.5 || l.CameraDistance > 20 {
		return bad(fmt.Sprintf("相机距离 %.3f m 超出 0.5~20 m", l.CameraDistance))
	}
	for _, f := range []struct {
		name string
		v    float64
	}{
		{"地板最远端", l.FloorFarLine},
		{"狗脚底线", l.DogGroundLine},
	} {
		if f.v <= 0 || f.v >= 1 {
			return bad(fmt.Sprintf("%s %.4f 必须在 0~1 之间（0=画面底，1=画面顶）", f.name, f.v))
		}
	}
	// 狗不能站得比地板最远端还高 —— 那是悬空 / 贴住墙的信号。
	if l.DogGroundLine > l.FloorFarLine {
		return bad(fmt.Sprintf("狗脚底线 %.4f 高于地板最远端 %.4f，狗会显得悬空或贴着墙脚",
			l.DogGroundLine, l.FloorFarLine))
	}
	if l.WanderHalfX <= 0 || l.WanderHalfX > 5 || l.WanderHalfZ <= 0 || l.WanderHalfZ > 5 {
		return bad(fmt.Sprintf("活动区半宽/半深 %.3f×%.3f 超出 0~5 m", l.WanderHalfX, l.WanderHalfZ))
	}
	if l.AmbientIntensity < 0 || l.AmbientIntensity > 3 {
		return bad(fmt.Sprintf("环境光强度 %.3f 超出 0~3", l.AmbientIntensity))
	}
	if l.ShadowOpacity < 0 || l.ShadowOpacity > 1 {
		return bad(fmt.Sprintf("假阴影不透明度 %.3f 超出 0~1", l.ShadowOpacity))
	}
	for _, c := range []struct {
		name string
		v    float64
	}{{"R", l.LightColorR}, {"G", l.LightColorG}, {"B", l.LightColorB}} {
		if c.v < 0 || c.v > 1 {
			return bad(fmt.Sprintf("主光颜色 %s=%.3f 超出 0~1", c.name, c.v))
		}
	}
	if l.LightEulerX < -90 || l.LightEulerX > 90 {
		return bad(fmt.Sprintf("主光俯角 %.1f° 超出 -90~90（超过就会从地面往上打）", l.LightEulerX))
	}
	return nil
}

// checkAssetPath 校验场景资源路径是安全的相对路径。
// 空串放行（表示该项不提供远程资源，由客户端走包内 key 或占位图）。
// 拒绝绝对 URL 与 .. 穿越：后台是运营可编辑的自由文本，不校验就可能被填成
// 任意外链，客户端下载时会把请求打到非预期的地址。
func checkAssetPath(p string) error {
	p = strings.TrimSpace(p)
	if p == "" {
		return nil
	}
	if strings.Contains(p, "..") {
		return fmt.Errorf("不能包含 ..")
	}
	if strings.HasPrefix(p, "/") || strings.HasPrefix(p, "\\") || strings.HasPrefix(p, "//") {
		return fmt.Errorf("必须是相对路径，不能以 / 开头")
	}
	if strings.Contains(p, "\\") {
		return fmt.Errorf("只能用 / 分隔")
	}
	lower := strings.ToLower(p)
	for _, bad := range []string{"http:", "https:", "data:", "javascript:", "file:"} {
		if strings.HasPrefix(lower, bad) {
			return fmt.Errorf("不能是 %s 开头的完整 URL", bad)
		}
	}
	return nil
}

func sortLevels(items []domain.LevelItem) {
	for i := 1; i < len(items); i++ {
		for j := i; j > 0 && items[j].Level < items[j-1].Level; j-- {
			items[j], items[j-1] = items[j-1], items[j]
		}
	}
}

// ---------- 发布 ----------

func (s *ConfigService) Publish(ctx context.Context, note, operator string) (int64, error) {
	draft, _, _, err := s.Draft(ctx)
	if err != nil {
		return 0, err
	}
	if err := ValidateSnapshot(draft); err != nil {
		return 0, err
	}
	changes, err := s.Diff(ctx)
	if err != nil {
		return 0, err
	}
	if len(changes) == 0 {
		return 0, domain.ErrParam("草稿与线上配置一致，无需发布")
	}
	return s.writeAndRelease(ctx, draft, changes, note, operator, "publish")
}

// Rollback 回滚到指定历史版本。版本号继续 +1（不回退计数），并记录 rollback 说明。
func (s *ConfigService) Rollback(ctx context.Context, targetVersion int64, note, operator string) (int64, error) {
	var rel domain.ConfigRelease
	err := s.db.C(store.CollConfigRelease).FindOne(ctx, store.F(store.D("version", targetVersion))).Decode(&rel)
	if err == mongo.ErrNoDocuments {
		return 0, domain.Errf(domain.CodeNotFound, "版本 %d 不存在", targetVersion)
	}
	if err != nil {
		return 0, err
	}
	var target domain.ConfigSnapshot
	if err := json.Unmarshal([]byte(rel.Payload), &target); err != nil {
		return 0, fmt.Errorf("历史版本解析失败: %w", err)
	}
	if err := ValidateSnapshot(&target); err != nil {
		return 0, err
	}
	effective, _, err := s.Effective(ctx)
	if err != nil {
		return 0, err
	}
	changes := DiffSnapshots(effective, &target)
	noteMsg := note
	if noteMsg == "" {
		noteMsg = fmt.Sprintf("回滚到 v%d", targetVersion)
	} else {
		noteMsg = fmt.Sprintf("回滚到 v%d：%s", targetVersion, note)
	}
	return s.writeAndRelease(ctx, &target, changes, noteMsg, operator, "rollback")
}

// writeAndRelease 把快照写回生效集合，然后版本 +1、留下快照、清理旧版。
//
// 无事务保障，顺序上把「版本号 +1」放在最后：中途失败时客户端拿到的仍是旧版本号，
// getConfig 会重新拉全量，不会出现「版本号已变但配置只写了一半」却不再拉取的情况。
func (s *ConfigService) writeAndRelease(ctx context.Context, snap *domain.ConfigSnapshot, changes []Change, note, operator, action string) (int64, error) {
	if err := s.writeLists(ctx, snap); err != nil {
		return 0, err
	}

	var meta domain.ConfigMeta
	err := s.db.C(store.CollConfigMeta).FindOne(ctx, store.F(store.D("_id", singletonID))).Decode(&meta)
	if err != nil && err != mongo.ErrNoDocuments {
		return 0, err
	}
	newVersion := meta.Version + 1
	if newVersion < 1 {
		newVersion = 1
	}
	meta = domain.ConfigMeta{
		ID: singletonID, Version: newVersion, UpdatedAt: timeutil.NowMs(),
		UpdatedBy: operator, Note: note,
	}
	if _, err := s.db.C(store.CollConfigMeta).ReplaceOne(ctx,
		store.F(store.D("_id", singletonID)), meta, options.Replace().SetUpsert(true)); err != nil {
		return 0, err
	}

	payload, _ := json.Marshal(snap)
	diffJSON, _ := json.Marshal(changes)
	rel := domain.ConfigRelease{
		ID: newID(), Version: newVersion, Payload: string(payload), Diff: string(diffJSON),
		Note: note, PublishedBy: operator, CreatedAt: timeutil.NowMs(),
	}
	if _, err := s.db.C(store.CollConfigRelease).InsertOne(ctx, rel); err != nil {
		return 0, err
	}

	s.pruneReleases(ctx)
	s.Invalidate()
	_ = s.DiscardDraft(ctx)
	return newVersion, nil
}

func (s *ConfigService) writeLists(ctx context.Context, snap *domain.ConfigSnapshot) error {
	if err := s.replaceList(ctx, store.CollConfigFood, toAnySlice(snap.Food), "id"); err != nil {
		return err
	}
	if err := s.replaceList(ctx, store.CollConfigScene, toAnySlice(snap.Scene), "id"); err != nil {
		return err
	}
	if err := s.replaceList(ctx, store.CollConfigAd, toAnySlice(snap.Ad), "id"); err != nil {
		return err
	}
	if err := s.replaceList(ctx, store.CollConfigLevel, toAnySlice(snap.Level), "level"); err != nil {
		return err
	}

	pd := pointsDoc{ID: singletonID, PointsConfig: snap.Points, Interaction: snap.Interaction}
	if _, err := s.db.C(store.CollConfigPoints).ReplaceOne(ctx,
		store.F(store.D("_id", singletonID)), pd, options.Replace().SetUpsert(true)); err != nil {
		return err
	}

	if _, err := s.db.C(store.CollConfigFlags).DeleteMany(ctx, bson.M{}); err != nil {
		return err
	}
	if len(snap.Flags) > 0 {
		docs := make([]any, 0, len(snap.Flags))
		for k, v := range snap.Flags {
			docs = append(docs, flagDoc{Key: k, Value: v})
		}
		if _, err := s.db.C(store.CollConfigFlags).InsertMany(ctx, docs); err != nil {
			return err
		}
	}
	return nil
}

// replaceList 逐条 upsert，再删掉快照里已不存在的项。
func (s *ConfigService) replaceList(ctx context.Context, coll string, docs []any, keyField string) error {
	keep := make([]any, 0, len(docs))
	for _, d := range docs {
		key := fieldValue(d, keyField)
		if key == nil {
			continue
		}
		keep = append(keep, key)
		if _, err := s.db.C(coll).ReplaceOne(ctx,
			store.F(store.D(keyField, key)), d, options.Replace().SetUpsert(true)); err != nil {
			return err
		}
	}
	filter := bson.M{}
	if len(keep) > 0 {
		filter = bson.M{keyField: bson.M{"$nin": keep}}
	}
	_, err := s.db.C(coll).DeleteMany(ctx, filter)
	return err
}

func toAnySlice[T any](in []T) []any {
	out := make([]any, 0, len(in))
	for i := range in {
		out = append(out, in[i])
	}
	return out
}

// fieldValue 用反射取结构体 json 名对应的值，避免为每种配置写一遍 switch。
func fieldValue(v any, jsonName string) any {
	raw, err := json.Marshal(v)
	if err != nil {
		return nil
	}
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		return nil
	}
	return m[jsonName]
}

func (s *ConfigService) pruneReleases(ctx context.Context) {
	cur, err := s.db.C(store.CollConfigRelease).Find(ctx, bson.M{},
		options.Find().SetSort(store.D("version", -1)).SetSkip(int64(s.releaseKe)))
	if err != nil {
		return
	}
	var old []domain.ConfigRelease
	if err := cur.All(ctx, &old); err != nil || len(old) == 0 {
		return
	}
	ids := make([]any, 0, len(old))
	for _, r := range old {
		ids = append(ids, r.ID)
	}
	_, _ = s.db.C(store.CollConfigRelease).DeleteMany(ctx, bson.M{"_id": bson.M{"$in": ids}})
}

// ---------- 历史版本 ----------

func (s *ConfigService) Releases(ctx context.Context, limit int64) ([]domain.ConfigRelease, error) {
	if limit <= 0 || limit > 100 {
		limit = 20
	}
	cur, err := s.db.C(store.CollConfigRelease).Find(ctx, bson.M{},
		options.Find().SetSort(store.D("version", -1)).SetLimit(limit).
			SetProjection(store.D("payload", 0)))
	if err != nil {
		return nil, err
	}
	var out []domain.ConfigRelease
	if err := cur.All(ctx, &out); err != nil {
		return nil, err
	}
	return out, nil
}

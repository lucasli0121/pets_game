package service

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

const singletonID = "singleton"

// pointsDoc config_points 与 config_level 之外的扁平配置集合的单文档载体。
type pointsDoc struct {
	ID                  string `bson:"_id"`
	domain.PointsConfig `bson:",inline"`
	Interaction         domain.InteractionConfig `bson:"interaction"`
}

type flagDoc struct {
	Key   string `bson:"key"`
	Value string `bson:"value"`
}

// ConfigService 配置的读、草稿、发布、回滚与 diff。
//
// 编辑态与发布态分离（文档 7.2）：运营改的是 config_draft 里的整份快照，
// 点发布才写回各 config_* 生效集合，同时把版本号 +1 并把快照留档到 config_release。
type ConfigService struct {
	db        *store.DB
	releaseKe int
	assetBase string

	mu       sync.RWMutex
	cached   *domain.ConfigSnapshot
	cachedV  int64
	cacheAt  time.Time
	cacheTTL time.Duration
}

func NewConfigService(db *store.DB, releaseKeep int, assetBase string) *ConfigService {
	if releaseKeep <= 0 {
		releaseKeep = 10
	}
	return &ConfigService{db: db, releaseKe: releaseKeep, assetBase: strings.TrimRight(assetBase, "/"), cacheTTL: 5 * time.Second}
}

// assetURL 把配置里的相对路径拼成可下载地址。
// assetBase 为空时原样返回，客户端据此判断「没有 CDN，只能走包内资源」。
func (s *ConfigService) assetURL(path string) string {
	if path == "" {
		return ""
	}
	if s.assetBase == "" {
		return path
	}
	return s.assetBase + "/" + strings.TrimLeft(path, "/")
}

// ClientSnapshot 读生效配置并补齐资源可下载地址，供下发给客户端。
// 只做副本，不改动缓存的快照。
func (s *ConfigService) ClientSnapshot(ctx context.Context) (*domain.ConfigSnapshot, int64, error) {
	snap, ver, err := s.Effective(ctx)
	if err != nil {
		return nil, 0, err
	}
	cp := *snap
	cp.Scene = make([]domain.SceneItem, len(snap.Scene))
	for i, sc := range snap.Scene {
		sc.ThumbURL = s.assetURL(sc.Thumb)
		sc.SceneURL = s.assetURL(sc.ScenePath)
		cp.Scene[i] = sc
	}
	return &cp, ver, nil
}

// ---------- 读生效态 ----------

func (s *ConfigService) Effective(ctx context.Context) (*domain.ConfigSnapshot, int64, error) {
	s.mu.RLock()
	if s.cached != nil && time.Since(s.cacheAt) < s.cacheTTL {
		snap, v := s.cached, s.cachedV
		s.mu.RUnlock()
		return snap, v, nil
	}
	s.mu.RUnlock()

	snap, v, err := s.loadEffective(ctx)
	if err != nil {
		return nil, 0, err
	}
	s.mu.Lock()
	s.cached, s.cachedV, s.cacheAt = snap, v, time.Now()
	s.mu.Unlock()
	return snap, v, nil
}

// Invalidate 发布/回滚后清缓存，保证同进程立刻读到新值。
func (s *ConfigService) Invalidate() {
	s.mu.Lock()
	s.cached = nil
	s.cacheAt = time.Time{}
	s.mu.Unlock()
}

func (s *ConfigService) loadEffective(ctx context.Context) (*domain.ConfigSnapshot, int64, error) {
	snap := &domain.ConfigSnapshot{Flags: map[string]string{}}

	food, err := s.loadList[domain.FoodItem](ctx, store.CollConfigFood)
	if err != nil {
		return nil, 0, err
	}
	snap.Food = food
	scene, err := s.loadList[domain.SceneItem](ctx, store.CollConfigScene)
	if err != nil {
		return nil, 0, err
	}
	snap.Scene = scene
	ads, err := s.loadList[domain.AdItem](ctx, store.CollConfigAd)
	if err != nil {
		return nil, 0, err
	}
	snap.Ad = ads
	lv, err := s.loadList[domain.LevelItem](ctx, store.CollConfigLevel)
	if err != nil {
		return nil, 0, err
	}
	snap.Level = lv

	var pd pointsDoc
	err = s.db.C(store.CollConfigPoints).FindOne(ctx, store.F(store.D("_id", singletonID))).Decode(&pd)
	if err != nil && err != mongo.ErrNoDocuments {
		return nil, 0, err
	}
	snap.Points = pd.PointsConfig
	snap.Interaction = pd.Interaction

	cur, err := s.db.C(store.CollConfigFlags).Find(ctx, bson.M{})
	if err != nil {
		return nil, 0, err
	}
	var flags []flagDoc
	if err := cur.All(ctx, &flags); err != nil {
		return nil, 0, err
	}
	for _, f := range flags {
		snap.Flags[f.Key] = f.Value
	}

	var meta domain.ConfigMeta
	err = s.db.C(store.CollConfigMeta).FindOne(ctx, store.F(store.D("_id", singletonID))).Decode(&meta)
	if err != nil && err != mongo.ErrNoDocuments {
		return nil, 0, err
	}

	normalizeSnapshot(snap)
	return snap, meta.Version, nil
}

func (s *ConfigService) loadList[T any](ctx context.Context, coll string) ([]T, error) {
	cur, err := s.db.C(coll).Find(ctx, bson.M{})
	if err != nil {
		return nil, err
	}
	var out []T
	if err := cur.All(ctx, &out); err != nil {
		return nil, err
	}
	return out, nil
}

func normalizeSnapshot(s *domain.ConfigSnapshot) {
	for i := range s.Scene {
		normalizeSceneLayout(&s.Scene[i].Layout)
	}
	sort.Slice(s.Food, func(i, j int) bool {
		if s.Food[i].Sort != s.Food[j].Sort {
			return s.Food[i].Sort < s.Food[j].Sort
		}
		return s.Food[i].ID < s.Food[j].ID
	})
	sort.Slice(s.Scene, func(i, j int) bool {
		if s.Scene[i].Sort != s.Scene[j].Sort {
			return s.Scene[i].Sort < s.Scene[j].Sort
		}
		return s.Scene[i].ID < s.Scene[j].ID
	})
	sort.Slice(s.Ad, func(i, j int) bool { return s.Ad[i].ID < s.Ad[j].ID })
	sort.Slice(s.Level, func(i, j int) bool { return s.Level[i].Level < s.Level[j].Level })
	if s.Flags == nil {
		s.Flags = map[string]string{}
	}
}

// ---------- 草稿 ----------

func (s *ConfigService) Draft(ctx context.Context) (*domain.ConfigSnapshot, string, int64, error) {
	var d domain.ConfigDraft
	err := s.db.C(store.CollConfigDraft).FindOne(ctx, store.F(store.D("_id", singletonID))).Decode(&d)
	if err == mongo.ErrNoDocuments {
		snap, _, err := s.Effective(ctx)
		return snap, "", 0, err
	}
	if err != nil {
		return nil, "", 0, err
	}
	var snap domain.ConfigSnapshot
	if err := json.Unmarshal([]byte(d.Payload), &snap); err != nil {
		return nil, "", 0, fmt.Errorf("草稿解析失败: %w", err)
	}
	normalizeSnapshot(&snap)
	return &snap, d.UpdatedBy, d.UpdatedAt, nil
}

func (s *ConfigService) SaveDraft(ctx context.Context, snap *domain.ConfigSnapshot, operator string) error {
	normalizeSnapshot(snap)
	raw, err := json.Marshal(snap)
	if err != nil {
		return err
	}
	_, err = s.db.C(store.CollConfigDraft).ReplaceOne(ctx,
		store.F(store.D("_id", singletonID)),
		domain.ConfigDraft{ID: singletonID, Payload: string(raw), UpdatedAt: timeutil.NowMs(), UpdatedBy: operator},
		options.Replace().SetUpsert(true))
	return err
}

func (s *ConfigService) DiscardDraft(ctx context.Context) error {
	_, err := s.db.C(store.CollConfigDraft).DeleteOne(ctx, store.F(store.D("_id", singletonID)))
	return err
}

// HasDraft 草稿是否存在（与生效态完全一致时也视为存在，由 diff 判断是否有改动）。
func (s *ConfigService) HasDraft(ctx context.Context) bool {
	n, err := s.db.C(store.CollConfigDraft).CountDocuments(ctx, bson.M{})
	return err == nil && n > 0
}

// ---------- diff ----------

// Change 一条配置差异。
type Change struct {
	Table  string `json:"table"`
	ItemID string `json:"itemId"`
	Field  string `json:"field"`
	Before any    `json:"before"`
	After  any    `json:"after"`
	Kind   string `json:"kind"` // added / removed / modified
}

// Diff 比较草稿与生效态（文档 7.2 发布前 diff）。
func (s *ConfigService) Diff(ctx context.Context) ([]Change, error) {
	effective, _, err := s.Effective(ctx)
	if err != nil {
		return nil, err
	}
	draft, _, _, err := s.Draft(ctx)
	if err != nil {
		return nil, err
	}
	return DiffSnapshots(effective, draft), nil
}

// DiffSnapshots 逐表逐 id 逐字段比对。
func DiffSnapshots(before, after *domain.ConfigSnapshot) []Change {
	var out []Change
	out = append(out, diffList("food", foodMap(before.Food), foodMap(after.Food))...)
	out = append(out, diffList("scene", sceneMap(before.Scene), sceneMap(after.Scene))...)
	out = append(out, diffList("ad", adMap(before.Ad), adMap(after.Ad))...)
	out = append(out, diffList("level", levelMap(before.Level), levelMap(after.Level))...)
	out = append(out, diffPoints(before.Points, after.Points)...)
	out = append(out, diffInteraction(before.Interaction, after.Interaction)...)
	out = append(out, diffFlags(before.Flags, after.Flags)...)
	sort.SliceStable(out, func(i, j int) bool {
		if out[i].Table != out[j].Table {
			return out[i].Table < out[j].Table
		}
		return out[i].ItemID < out[j].ItemID
	})
	return out
}

func foodMap(items []domain.FoodItem) map[string]map[string]any {
	m := make(map[string]map[string]any, len(items))
	for _, it := range items {
		m[it.ID] = map[string]any{
			"name": it.Name, "category": it.Category, "price": it.Price,
			"satiety": it.Satiety, "happiness": it.Happiness, "exp": it.Exp,
			"icon": it.Icon, "sort": it.Sort, "enabled": it.Enabled, "minLevel": it.MinLevel,
		}
	}
	return m
}

func sceneMap(items []domain.SceneItem) map[string]map[string]any {
	m := make(map[string]map[string]any, len(items))
	for _, it := range items {
		entry := map[string]any{
			"name": it.Name, "price": it.Price, "thumb": it.Thumb,
			"sceneKey": it.SceneKey, "sort": it.Sort, "enabled": it.Enabled, "minLevel": it.MinLevel,
		}
		// layout 必须参与 diff：它决定客户端相机怎么摆，
		// 改了不显示在发布 diff 里，运营会以为没生效。
		// scenePath 一直不在 diff 里（它只拼 URL，且 apidoc 已注明 URL 类字段不参与 diff）。
		for k, v := range structToMap(it.Layout) {
			entry["layout."+k] = v
		}
		m[it.ID] = entry
	}
	return m
}

func adMap(items []domain.AdItem) map[string]map[string]any {
	m := make(map[string]map[string]any, len(items))
	for _, it := range items {
		m[it.ID] = map[string]any{
			"adUnitId": it.AdUnitID, "adType": it.AdType, "placement": it.Placement,
			"enabled": it.Enabled, "minIntervalSec": it.MinIntervalSec,
			"dailyLimit": it.DailyLimit, "skipFirstDay": it.SkipFirstDay,
		}
	}
	return m
}

func levelMap(items []domain.LevelItem) map[string]map[string]any {
	m := make(map[string]map[string]any, len(items))
	for _, it := range items {
		m[fmt.Sprintf("%d", it.Level)] = map[string]any{
			"expRequired": it.ExpRequired, "rewardPoints": it.RewardPoints,
			"scale": it.Scale, "enabled": it.Enabled, "note": it.Note,
		}
	}
	return m
}

func diffList(table string, before, after map[string]map[string]any) []Change {
	var out []Change
	for id, b := range before {
		a, ok := after[id]
		if !ok {
			out = append(out, Change{Table: table, ItemID: id, Field: "*", Before: "存在", After: "已删除", Kind: "removed"})
			continue
		}
		for f, bv := range b {
			if av, ok := a[f]; ok && !equalJSON(bv, av) {
				out = append(out, Change{Table: table, ItemID: id, Field: f, Before: bv, After: av, Kind: "modified"})
			}
		}
	}
	for id := range after {
		if _, ok := before[id]; !ok {
			out = append(out, Change{Table: table, ItemID: id, Field: "*", Before: "不存在", After: "新增", Kind: "added"})
		}
	}
	return out
}

func diffPoints(b, a domain.PointsConfig) []Change {
	bm := structToMap(b)
	am := structToMap(a)
	return compareFlat("points", "points", bm, am)
}

func diffInteraction(b, a domain.InteractionConfig) []Change {
	return compareFlat("interaction", "interaction", structToMap(b), structToMap(a))
}

func compareFlat(table, itemID string, bm, am map[string]any) []Change {
	var out []Change
	for f, bv := range bm {
		if av, ok := am[f]; ok && !equalJSON(bv, av) {
			out = append(out, Change{Table: table, ItemID: itemID, Field: f, Before: bv, After: av, Kind: "modified"})
		}
	}
	return out
}

func diffFlags(b, a map[string]string) []Change {
	var out []Change
	for k, bv := range b {
		av, ok := a[k]
		if !ok {
			out = append(out, Change{Table: "flags", ItemID: k, Field: "value", Before: bv, After: "已删除", Kind: "removed"})
			continue
		}
		if av != bv {
			out = append(out, Change{Table: "flags", ItemID: k, Field: "value", Before: bv, After: av, Kind: "modified"})
		}
	}
	for k, av := range a {
		if _, ok := b[k]; !ok {
			out = append(out, Change{Table: "flags", ItemID: k, Field: "value", Before: "不存在", After: av, Kind: "added"})
		}
	}
	return out
}

func structToMap(v any) map[string]any {
	raw, _ := json.Marshal(v)
	m := map[string]any{}
	_ = json.Unmarshal(raw, &m)
	return m
}

// normalizeSceneLayout 把没填的构图数据点补成默认值。
//
// 用「值为 0 视为未填」而不是显式的 hasXxx 标记：
// 少一个标记位就少一处可能漏掉的同步点。构图参数没有「合法的 0」
// （orthoSize=0 会让画面全空，floorFarLine=0 会让狗站到画面底外）。
func normalizeSceneLayout(l *domain.SceneLayout) {
	d := domain.DefaultSceneLayout()
	if l.OrthographicSize <= 0 {
		l.OrthographicSize = d.OrthographicSize
	}
	if l.CameraPitch == 0 {
		l.CameraPitch = d.CameraPitch
	}
	if l.CameraDistance <= 0 {
		l.CameraDistance = d.CameraDistance
	}
	if l.FloorFarLine <= 0 {
		l.FloorFarLine = d.FloorFarLine
	}
	if l.DogGroundLine <= 0 {
		l.DogGroundLine = d.DogGroundLine
	}
	if l.WanderHalfX <= 0 {
		l.WanderHalfX = d.WanderHalfX
	}
	if l.WanderHalfZ <= 0 {
		l.WanderHalfZ = d.WanderHalfZ
	}
	if l.LightColorR == 0 && l.LightColorG == 0 && l.LightColorB == 0 {
		l.LightColorR, l.LightColorG, l.LightColorB = d.LightColorR, d.LightColorG, d.LightColorB
	}
	if l.AmbientIntensity <= 0 {
		l.AmbientIntensity = d.AmbientIntensity
	}
	if l.ShadowOpacity <= 0 {
		l.ShadowOpacity = d.ShadowOpacity
	}
}

func equalJSON(a, b any) bool {
	ab, _ := json.Marshal(a)
	bb, _ := json.Marshal(b)
	return string(ab) == string(bb)
}

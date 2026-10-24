package service

import (
	"context"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

type AdService struct {
	db     *store.DB
	cfg    *ConfigService
	assets *AssetsService
}

func NewAdService(db *store.DB, cfg *ConfigService, assets *AssetsService) *AdService {
	return &AdService{db: db, cfg: cfg, assets: assets}
}

type AdClaimResult struct {
	Reward         int    `json:"reward"`
	Balance        int    `json:"balance"`
	RejectedReason string `json:"rejectedReason"`
	RemainToday    int    `json:"remainToday"`
}

// ClaimReward 激励视频发奖（文档 6.2）。
//
// 不信任 client_completed，只作为参考记进 ad_events。真正的兜底是三件事：
// 每日上限、最小间隔、request_id 只能发一次奖（由 IdemService 保证）。
func (s *AdService) ClaimReward(ctx context.Context, userID, adUnit, placement string,
	clientCompleted bool) (*AdClaimResult, error) {

	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	out := &AdClaimResult{}

	as, err := s.assets.Get(ctx, userID, defaultSceneID(snap))
	if err != nil {
		return nil, err
	}
	out.Balance = as.Points.Balance

	if snap.Flags["enable_ads"] != "true" {
		out.RejectedReason = "广告功能已关闭"
		return out, nil
	}

	item, ok := findAd(snap, adUnit, placement, domain.AdTypeRewarded)
	if !ok || !item.Enabled {
		out.RejectedReason = "广告位未启用"
		return out, nil
	}

	now := timeutil.NowMs()
	dayStart := timeutil.DayStart(now)

	// 每日上限：广告位自身配了就用它，否则回落到 config_points.ad_daily_limit
	limit := item.DailyLimit
	if limit <= 0 {
		limit = snap.Points.AdDailyLimit
	}
	todayCount, err := s.db.C(store.CollAdEvents).CountDocuments(ctx, store.F(
		"user_id", userID,
		"ad_type", domain.AdTypeRewarded,
		"rewarded", true,
		"created_at", bson.M{"$gte": dayStart},
	))
	if err != nil {
		return nil, err
	}
	remain := 0
	if limit > 0 {
		remain = limit - int(todayCount)
		if remain < 0 {
			remain = 0
		}
	}
	out.RemainToday = remain
	if limit > 0 && todayCount >= int64(limit) {
		out.RejectedReason = "今日广告奖励次数已用完"
		return out, nil
	}

	// 最小间隔
	if item.MinIntervalSec > 0 {
		var last domain.AdEvent
		err := s.db.C(store.CollAdEvents).FindOne(ctx,
			store.F("user_id", userID, "ad_type", domain.AdTypeRewarded, "rewarded", true),
			options.FindOne().SetSort(store.D("created_at", -1))).Decode(&last)
		if err == nil && now-last.CreatedAt < int64(item.MinIntervalSec)*1000 {
			wait := (int64(item.MinIntervalSec)*1000 - (now - last.CreatedAt)) / 1000
			if wait < 1 {
				wait = 1
			}
			out.RejectedReason = "广告播放过于频繁，请稍后重试"
			return out, nil
		}
		if err != nil && err != mongo.ErrNoDocuments {
			return nil, err
		}
	}

	reward := snap.Points.AdReward
	ev := domain.AdEvent{
		ID: newID(), UserID: userID, AdUnit: item.AdUnitID, AdType: domain.AdTypeRewarded,
		Placement: placement, Completed: clientCompleted, Rewarded: false,
		Reward: reward, CreatedAt: now,
	}
	if _, err := s.db.C(store.CollAdEvents).InsertOne(ctx, ev); err != nil {
		return nil, err
	}

	balance, ledgerID, err := s.assets.Grant(ctx, userID, reward, domain.ReasonAdReward,
		ev.ID, "", "激励视频 "+item.Placement)
	if err != nil {
		// 发奖失败：把记录标记为未发奖，保留证据供对账
		_, _ = s.db.C(store.CollAdEvents).UpdateOne(ctx, store.F("_id", ev.ID),
			bson.D{{Key: "$set", Value: bson.D{{Key: "completed", Value: clientCompleted}}}})
		return nil, err
	}
	_, _ = s.db.C(store.CollAdEvents).UpdateOne(ctx, store.F("_id", ev.ID),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "rewarded", Value: true},
			{Key: "ledger_id", Value: ledgerID},
		}}})

	out.Reward, out.Balance = reward, balance
	LogAction(ctx, s.db, userID, "", ActionAdReward, placement)
	return out, nil
}

type AdImpressionResult struct {
	Accepted bool `json:"accepted"`
}

// Impression 记录插屏曝光（不发奖，文档 13.8 ②）。
// 只写 ad_events 供看板统计完播率与人均观看次数。
func (s *AdService) Impression(ctx context.Context, userID, adUnit, placement string, completed bool) (*AdImpressionResult, error) {
	snap, _, err := s.cfg.Effective(ctx)
	if err != nil {
		return nil, err
	}
	if snap.Flags["enable_ads"] != "true" {
		return &AdImpressionResult{Accepted: false}, nil
	}
	item, ok := findAd(snap, adUnit, placement, domain.AdTypeInterstitial)
	if !ok || !item.Enabled {
		return &AdImpressionResult{Accepted: false}, nil
	}
	ev := domain.AdEvent{
		ID: newID(), UserID: userID, AdUnit: item.AdUnitID, AdType: domain.AdTypeInterstitial,
		Placement: placement, Completed: completed, Rewarded: false,
		Reward: 0, CreatedAt: timeutil.NowMs(),
	}
	if _, err := s.db.C(store.CollAdEvents).InsertOne(ctx, ev); err != nil {
		return nil, err
	}
	return &AdImpressionResult{Accepted: true}, nil
}

func findAd(snap *domain.ConfigSnapshot, adUnit, placement, adType string) (domain.AdItem, bool) {
	// 精确匹配 adUnit；为空时按 placement + 类型退化匹配
	for _, it := range snap.Ad {
		if adUnit != "" && it.AdUnitID == adUnit {
			return it, true
		}
	}
	for _, it := range snap.Ad {
		if it.AdType == adType && (placement == "" || it.Placement == placement) {
			return it, true
		}
	}
	return domain.AdItem{}, false
}

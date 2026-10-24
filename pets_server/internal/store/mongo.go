package store

import (
	"context"
	"fmt"
	"os"
	"sync"
	"time"

	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"
	"go.mongodb.org/mongo-driver/v2/mongo/readpref"
)

// 集合名
const (
	CollUsers         = "users"
	CollPets          = "pets"
	CollAssets        = "user_assets" // 积分 + 库存 + 场景解锁（聚合根）
	CollLedger        = "point_ledger"
	CollCheckIn       = "daily_checkin"
	CollAdEvents      = "ad_events"
	CollOrders        = "orders"
	CollDedup         = "request_dedup"
	CollConfigFood    = "config_food"
	CollConfigScene   = "config_scene"
	CollConfigPoints  = "config_points"
	CollConfigAd      = "config_ad"
	CollConfigFlags   = "config_flags"
	CollConfigLevel   = "config_level"
	CollConfigMeta    = "config_meta"
	CollConfigDraft   = "config_draft"
	CollConfigRelease = "config_release"
	CollAdminUsers    = "admin_users"
	CollAuditLog      = "admin_audit_log"
	CollEventLog      = "event_log"    // reportEvent 埋点
	CollDailyActive   = "daily_active" // (user_id, date) 用于留存口径
	CollActionLog     = "action_log"   // 喂食/互动/切换等行为计数
)

type DB struct {
	Client *mongo.Client
	DB     *mongo.Database
}

// Open 连接 MongoDB 并建索引。
func Open(ctx context.Context, uri, dbName string) (*DB, error) {
	cli, err := mongo.Connect(options.Client().
		ApplyURI(uri).
		SetServerSelectionTimeout(10 * time.Second).
		SetConnectTimeout(10 * time.Second).
		SetMaxPoolSize(50))
	if err != nil {
		return nil, fmt.Errorf("mongo connect: %w", err)
	}
	if err := cli.Ping(ctx, readpref.Primary()); err != nil {
		return nil, fmt.Errorf("mongo ping: %w", err)
	}
	d := &DB{Client: cli, DB: cli.Database(dbName)}
	if err := d.ensureIndexes(ctx); err != nil {
		return nil, err
	}
	return d, nil
}

func (d *DB) Close(ctx context.Context) error { return d.Client.Disconnect(ctx) }

func (d *DB) C(name string) *mongo.Collection { return d.DB.Collection(name) }

// IsDuplicate 判断是否为唯一索引冲突（幂等与防重的核心判据）。
func IsDuplicate(err error) bool {
	return mongo.IsDuplicateKeyError(err)
}

var debugIndex = os.Getenv("DEBUG_INDEX") != ""

type idxSpec struct {
	coll   string
	models []mongo.IndexModel
}

func (d *DB) ensureIndexes(ctx context.Context) error {
	u := func(name string) *options.IndexOptionsBuilder {
		b := options.Index()
		b.SetName(name)
		b.SetUnique(true)
		return b
	}
	n := func(name string) *options.IndexOptionsBuilder {
		b := options.Index()
		b.SetName(name)
		return b
	}

	specs := []idxSpec{
		{CollUsers, []mongo.IndexModel{
			{Keys: D("openid", 1), Options: u("uk_openid")},
			{Keys: D("created_at", -1), Options: n("idx_created")},
			{Keys: D("last_login_at", -1), Options: n("idx_last_login")},
		}},
		{CollPets, []mongo.IndexModel{
			{Keys: D("user_id", 1), Options: n("idx_user")},
			{Keys: D("user_id", 1, "is_active", 1), Options: n("idx_user_active")},
		}},
		{CollAssets, []mongo.IndexModel{
			{Keys: D("points.balance", 1), Options: n("idx_balance")},
		}},
		{CollLedger, []mongo.IndexModel{
			{Keys: D("user_id", 1, "created_at", -1), Options: n("idx_user_created")},
			{Keys: D("created_at", -1), Options: n("idx_created")},
			{Keys: D("reason", 1, "created_at", -1), Options: n("idx_reason_created")},
			{Keys: D("request_id", 1), Options: n("idx_request")},
		}},
		{CollCheckIn, []mongo.IndexModel{
			{Keys: D("user_id", 1, "date", 1), Options: u("uk_user_date")},
			{Keys: D("date", 1), Options: n("idx_date")},
		}},
		{CollAdEvents, []mongo.IndexModel{
			{Keys: D("user_id", 1, "created_at", -1), Options: n("idx_user_created")},
			{Keys: D("ad_type", 1, "created_at", -1), Options: n("idx_type_created")},
			{Keys: D("placement", 1, "created_at", -1), Options: n("idx_placement_created")},
		}},
		{CollOrders, []mongo.IndexModel{
			{Keys: D("order_id", 1), Options: u("uk_order_id")},
			{Keys: D("user_id", 1, "created_at", -1), Options: n("idx_user_created")},
			{Keys: D("type", 1, "created_at", -1), Options: n("idx_type_created")},
		}},
		{CollConfigFood, []mongo.IndexModel{
			{Keys: D("id", 1), Options: u("uk_id")},
		}},
		{CollConfigScene, []mongo.IndexModel{
			{Keys: D("id", 1), Options: u("uk_id")},
		}},
		{CollConfigAd, []mongo.IndexModel{
			{Keys: D("id", 1), Options: u("uk_id")},
		}},
		{CollConfigLevel, []mongo.IndexModel{
			{Keys: D("level", 1), Options: u("uk_level")},
		}},
		{CollConfigFlags, []mongo.IndexModel{
			{Keys: D("key", 1), Options: u("uk_key")},
		}},
		{CollConfigRelease, []mongo.IndexModel{
			{Keys: D("version", 1), Options: u("uk_version")},
		}},
		{CollAdminUsers, []mongo.IndexModel{
			{Keys: D("username", 1), Options: u("uk_username")},
		}},
		{CollAuditLog, []mongo.IndexModel{
			{Keys: D("created_at", -1), Options: n("idx_created")},
			{Keys: D("admin_id", 1, "created_at", -1), Options: n("idx_admin_created")},
		}},
		{CollDailyActive, []mongo.IndexModel{
			{Keys: D("user_id", 1, "date", 1), Options: u("uk_user_date")},
			{Keys: D("date", 1), Options: n("idx_date")},
		}},
		{CollActionLog, []mongo.IndexModel{
			{Keys: D("user_id", 1, "created_at", -1), Options: n("idx_user_created")},
			{Keys: D("action", 1, "created_at", -1), Options: n("idx_action_created")},
		}},
		{CollEventLog, []mongo.IndexModel{
			{Keys: D("user_id", 1, "created_at", -1), Options: n("idx_user_created")},
			{Keys: D("event", 1, "created_at", -1), Options: n("idx_event_created")},
		}},
	}

	// 幂等记录：唯一键 + 30 天 TTL（过期自动清理，避免无限增长）
	dedupTTL := int32(30 * 24 * 3600)
	ttlOpt := options.Index()
	ttlOpt.SetName("uk_user_request")
	ttlOpt.SetUnique(true)
	ttlExp := options.Index()
	ttlExp.SetName("ttl_created")
	ttlExp.SetExpireAfterSeconds(dedupTTL)
	specs = append(specs, idxSpec{coll: CollDedup, models: []mongo.IndexModel{
		{Keys: D("user_id", 1, "request_id", 1), Options: ttlOpt},
		{Keys: D("created_at", 1), Options: ttlExp},
	}})

	return d.createIndexesParallel(ctx, specs)
}

// createIndexesParallel 并发建索引。
//
// 为什么并发：对每个「还不存在的集合」，MongoDB 的 createIndexes 会顺带建集合，
// 单次要 300–800ms（创建集合本身的开销，与网络无关）。串行 18 个集合启动要 9 秒，
// 并发（8 路）降到 1 秒级。不同集合互不影响，并发是安全的。
func (d *DB) createIndexesParallel(ctx context.Context, specs []idxSpec) error {
	const workers = 8
	sem := make(chan struct{}, workers)
	var wg sync.WaitGroup
	var mu sync.Mutex
	var firstErr error

	for _, s := range specs {
		wg.Add(1)
		sem <- struct{}{}
		go func(s idxSpec) {
			defer wg.Done()
			defer func() { <-sem }()
			t0 := time.Now()
			_, err := d.C(s.coll).Indexes().CreateMany(ctx, s.models)
			if debugIndex {
				fmt.Printf("[index] %-20s %v err=%v\n", s.coll, time.Since(t0), err)
			}
			if err != nil {
				mu.Lock()
				if firstErr == nil {
					firstErr = fmt.Errorf("index %s: %w", s.coll, err)
				}
				mu.Unlock()
			}
		}(s)
	}
	wg.Wait()
	return firstErr
}

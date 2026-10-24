package service

import (
	"context"
	"encoding/json"
	"time"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"

	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

// IdemService 幂等去重（文档第 5 节：入参统一含 request_id，按 (user_id, request_id) 去重）。
//
// 实现方式：唯一索引 (user_id, request_id) 插入成功者才执行业务，并发重复请求只能插失败。
// 与文档 6.1 的「乐观锁重试」互补：这里保证同一笔请求只生效一次，
// assets.go 里的条件更新保证余额不会被扣穿。
type IdemService struct{ db *store.DB }

func NewIdemService(db *store.DB) *IdemService { return &IdemService{db: db} }

// Run 执行带幂等的业务函数。replayed=true 表示命中了首次结果。
func (s *IdemService) Run(ctx context.Context, userID, requestID, endpoint string,
	fn func(context.Context) (any, error)) (data any, replayed bool, err error) {

	if requestID == "" {
		d, e := fn(ctx)
		return d, false, e
	}

	doc := domain.Dedup{
		ID: newID(), UserID: userID, RequestID: requestID,
		Endpoint: endpoint, State: "processing", CreatedAt: timeutil.NowMs(),
	}
	_, insErr := s.db.C(store.CollDedup).InsertOne(ctx, doc)
	if insErr != nil {
		if !store.IsDuplicate(insErr) {
			return nil, false, insErr
		}
		// 并发重复：等首次请求落库
		for i := 0; i < 6; i++ {
			var prev domain.Dedup
			e := s.db.C(store.CollDedup).FindOne(ctx,
				store.F("user_id", userID, "request_id", requestID)).Decode(&prev)
			if e == nil && prev.State == "done" && prev.Response != "" {
				var out any
				if jsonErr := json.Unmarshal([]byte(prev.Response), &out); jsonErr != nil {
					return nil, false, jsonErr
				}
				return out, true, nil
			}
			if e == mongo.ErrNoDocuments {
				break
			}
			select {
			case <-ctx.Done():
				return nil, false, ctx.Err()
			case <-time.After(120 * time.Millisecond):
			}
		}
		return nil, false, domain.Err(domain.CodeProcessing, "请求处理中，请稍后重试")
	}

	out, fnErr := fn(ctx)
	if fnErr != nil {
		// 业务失败不缓存，允许用户修正后重试同一 request_id
		_, _ = s.db.C(store.CollDedup).DeleteOne(ctx, store.F("_id", doc.ID))
		return nil, false, fnErr
	}

	raw, mErr := json.Marshal(out)
	if mErr != nil {
		return out, false, nil
	}
	_, _ = s.db.C(store.CollDedup).UpdateOne(ctx,
		store.F("_id", doc.ID),
		bson.D{{Key: "$set", Value: bson.D{
			{Key: "state", Value: "done"},
			{Key: "response", Value: string(raw)},
		}}})
	return out, false, nil
}

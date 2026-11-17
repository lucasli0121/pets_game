package test

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/app"
	"pets_server/internal/config"
	"pets_server/internal/domain"
	"pets_server/internal/timeutil"
)

// 端到端测试：起真实 MongoDB（独立测试库）+ 完整 HTTP 栈。
// 每个用例一个独立库，跑完即删，互不干扰。

func mongoURI() string {
	if v := os.Getenv("MONGO_URI"); v != "" {
		return v
	}
	return "mongodb://localhost:27017/?authSource=admin"
}

func testDBName(t *testing.T) string {
	repl := strings.NewReplacer("/", "_", " ", "_", "-", "_")
	return "pets_test_" + repl.Replace(strings.ToLower(t.Name()))
}

// 所有用例共用同一个测试库，靠「清空所有集合数据」做隔离。
//
// 为什么不每个用例建一个库：MongoDB 建集合本身在服务端是串行的，每个新集合约 500ms，
// 18 个集合就要 9 秒。每用例一个库 = 每用例付 9 秒；共用一个库则只在进程内首次付一次，
// 后续用例只花 100ms 清数据。用例是顺序执行的，不存在互相干扰。
const testDB = "pets_test_shared"

func newApp(t *testing.T, tweak func(*config.Config)) (*app.App, *httptest.Server) {
	t.Helper()

	cfg := &config.Config{
		AppName: "test", Addr: ":0", Debug: true,
		MongoURI: mongoURI(), MongoDB: testDB,
		JWTSecret: "test-secret-key", ClientTokenTTLH: 24, AdminTokenTTLH: 24,
		AllowDevLogin:      true,
		RateLimitWindowSec: 10, RateLimitMaxCalls: 10000,
		AdminInitUser: "admin", AdminInitPass: "admin",
		ReleaseKeep: 10,
	}
	if tweak != nil {
		tweak(cfg)
	}

	purgeAll(t, cfg.MongoURI, testDB)

	a, err := app.Build(context.Background(), cfg)
	if err != nil {
		t.Fatalf("构建应用失败: %v", err)
	}
	srv := httptest.NewServer(a.Handler)
	t.Cleanup(func() {
		srv.Close()
		a.Close()
	})
	return a, srv
}

// purgeAll 清空所有集合的文档，保留集合与索引。
func purgeAll(t *testing.T, uri, dbName string) {
	t.Helper()
	cli, err := newRawClient(uri)
	if err != nil {
		t.Fatalf("连接 Mongo 失败: %v", err)
	}
	defer cli.Disconnect(context.Background())

	ctx := context.Background()
	db := cli.Database(dbName)
	names, err := db.ListCollectionNames(ctx, bson.M{})
	if err != nil {
		return
	}
	for _, n := range names {
		if _, err := db.Collection(n).DeleteMany(ctx, bson.M{}); err != nil {
			t.Fatalf("清空集合 %s 失败: %v", n, err)
		}
	}
}

// ---------- HTTP 小工具 ----------

type envelope struct {
	Code int             `json:"code"`
	Msg  string          `json:"msg"`
	Data json.RawMessage `json:"data"`
}

func call(t *testing.T, srv *httptest.Server, method, path, token string, body any) (envelope, map[string]any) {
	t.Helper()
	var buf io.Reader
	if body != nil {
		raw, _ := json.Marshal(body)
		buf = bytes.NewReader(raw)
	}
	req, err := http.NewRequest(method, srv.URL+path, buf)
	if err != nil {
		t.Fatalf("构造请求失败: %v", err)
	}
	req.Header.Set("Content-Type", "application/json")
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("请求失败 %s %s: %v", method, path, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(resp.Body)

	var env envelope
	if err := json.Unmarshal(raw, &env); err != nil {
		t.Fatalf("响应不是 JSON（%s %s, http=%d）: %s", method, path, resp.StatusCode, string(raw))
	}
	var data map[string]any
	if len(env.Data) > 0 {
		_ = json.Unmarshal(env.Data, &data)
	}
	return env, data
}

func mustOK(t *testing.T, env envelope, data map[string]any, what string) map[string]any {
	t.Helper()
	if env.Code != 0 {
		t.Fatalf("%s 期望成功，实际 code=%d msg=%s", what, env.Code, env.Msg)
	}
	return data
}

func login(t *testing.T, srv *httptest.Server, openid string) (string, map[string]any) {
	t.Helper()
	env, data := call(t, srv, "POST", "/api/v1/login", "", map[string]any{
		"devOpenid": openid, "nickname": "测试用户",
	})
	mustOK(t, env, data, "login")
	tok, _ := data["token"].(string)
	if tok == "" {
		t.Fatalf("login 未返回 token: %v", data)
	}
	return tok, data
}

// adopt 完成首次领养，返回 petId
func adopt(t *testing.T, srv *httptest.Server, tok string) string {
	t.Helper()
	env, data := call(t, srv, "POST", "/api/v1/pet/adopt", tok, map[string]any{
		"petType": "dog", "name": "小白",
		"personality": map[string]any{"lively": 7, "curiosity": 6, "affection": 8, "independence": 4},
	})
	mustOK(t, env, data, "adoptPet")
	pet, _ := data["pet"].(map[string]any)
	id, _ := pet["id"].(string)
	if id == "" {
		t.Fatalf("adopt 未返回宠物 id: %v", data)
	}
	return id
}

func num(v any) int {
	switch n := v.(type) {
	case float64:
		return int(n)
	case int:
		return n
	case int32:
		return int(n)
	case int64:
		return int(n)
	}
	return 0
}

func pid(t *testing.T, a *app.App, openid string) string {
	t.Helper()
	var u domain.User
	if err := a.DB.C("users").FindOne(context.Background(),
		bson.M{"openid": "dev_" + openid}).Decode(&u); err != nil {
		t.Fatalf("查用户失败: %v", err)
	}
	return u.ID
}

func nowMS() int64 { return timeutil.NowMs() }

func newRawClient(uri string) (*mongo.Client, error) {
	return mongo.Connect(options.Client().
		ApplyURI(uri).
		SetServerSelectionTimeout(8 * time.Second).
		SetConnectTimeout(8 * time.Second))
}

var _ = fmt.Sprintf
var _ = time.Now

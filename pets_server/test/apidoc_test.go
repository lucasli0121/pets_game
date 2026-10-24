package test

// 接口文档与路由的一致性测试。
//
// 为什么需要这个测试：接口规格（internal/api/apidoc.go）是**人写的声明**，
// 不是从代码反射生成的。加了新路由但忘了写文档，编译不会报错、运行也不会报错，
// 只有对接的客户端照着文档调才发现少一个接口。这里把两条锁在一起。

import (
	"encoding/json"
	"strings"
	"testing"

	"pets_server/internal/api"
	"pets_server/internal/domain"
)

// TestDocs_规格与路由一一对应 逐条比对方法 + 路径。
func TestDocs_规格与路由一一对应(t *testing.T) {
	specs := api.APISpecs()
	if len(specs) == 0 {
		t.Fatal("接口规格表为空，文档页会渲染出空白")
	}

	// 规格表里不能有重复项（复制粘贴时最容易犯）。
	seen := map[string]string{}
	for _, s := range specs {
		key := s.Method + " " + s.Path
		if prev, dup := seen[key]; dup {
			t.Errorf("接口规格重复：%s（与 %s 撞了）", key, prev)
		}
		seen[key] = s.Group

		if s.Title == "" {
			t.Errorf("%s 缺标题", key)
		}
		if s.Group != "客户端" && s.Group != "管理端" {
			t.Errorf("%s 的分组非法：%q", key, s.Group)
		}
		// 客户端接口必须在 /api/v1 下，管理端接口必须在 /api/admin 下，
		// 否则文档里的分组会骗人。
		switch s.Group {
		case "客户端":
			if !strings.HasPrefix(s.Path, "/api/v1/") {
				t.Errorf("客户端接口路径应在 /api/v1/ 下：%s", key)
			}
		case "管理端":
			if !strings.HasPrefix(s.Path, "/api/admin/") {
				t.Errorf("管理端接口路径应在 /api/admin/ 下：%s", key)
			}
		}
		// 幂等接口必须说明 requestId，否则客户端不知道要传。
		if s.Idem && !hasField(s.Req, "requestId") {
			t.Errorf("%s 标了幂等，但请求字段里没有 requestId", key)
		}
	}
}

// TestDocs_路由表覆盖 路由注册表里的每条接口都必须在规格表里出现。
func TestDocs_路由表覆盖(t *testing.T) {
	a, _ := newApp(t, nil)
	routes := a.API.RouteTable()
	specs := api.APISpecs()
	if len(routes) == 0 {
		t.Fatal("路由表为空，说明 Routes() 没跑过或注册方式被改坏了")
	}

	inSpec := map[string]bool{}
	for _, s := range specs {
		inSpec[s.Method+" "+s.Path] = true
	}

	for _, r := range routes {
		key := r.Method + " " + r.Path
		if !inSpec[key] {
			t.Errorf("路由已注册但接口文档里没有：%s", key)
		}
		delete(inSpec, key)
	}
	// 剩下的说明规格表里写了不存在的路由。
	for key := range inSpec {
		t.Errorf("接口文档里有路由表里不存在的条目：%s", key)
	}
}

// TestDocs_示例是合法JSON 文档里的请求示例会被管理端「在线调试」直接发出去，
// 写错字段名（比如把 curiosity 写成 curious）会让使用方直接踩坑，
// 而 go 编译发现不了字符串里的错。这条把示例全跑一遍 JSON 解析。
func TestDocs_示例是合法JSON(t *testing.T) {
	for _, s := range api.APISpecs() {
		key := s.Method + " " + s.Path
		if s.Example == "" {
			continue
		}
		// GET 接口的示例是 query string（?a=1&b=2），不是 JSON 文档。
		if s.Method == "GET" {
			continue
		}
		var v map[string]any
		if err := json.Unmarshal([]byte(s.Example), &v); err != nil {
			t.Errorf("%s 的示例不是合法 JSON：%v\n  %s", key, err, s.Example)
		}
	}
}

// TestDocs_示例字段名对得上真实结构 抓「文档写 curious、代码要 curiosity」这类错。
//
// 服务端 decode 开了 DisallowUnknownFields，字段名拼错会直接报 1001，
// 所以文档里的字段名必须与 Go 结构体的 json tag 完全一致。
// 这类错误编译期发现不了（都在字符串里），只有真去调一次才会暴露。
func TestDocs_示例字段名对得上真实结构(t *testing.T) {
	for _, s := range api.APISpecs() {
		if !strings.Contains(s.Path, "/personality") && !strings.Contains(s.Path, "/adopt") {
			continue
		}
		var v map[string]any
		if err := json.Unmarshal([]byte(s.Example), &v); err != nil {
			continue // 上面 TestDocs_示例是合法JSON 已经报了
		}
		raw, err := json.Marshal(v["personality"])
		if err != nil {
			continue
		}
		// 用严格模式反序列化：DisallowUnknownFields 与服务端 decode 行为一致，
		// 字段名对不上会在这里报出来。
		dec := json.NewDecoder(strings.NewReader(string(raw)))
		dec.DisallowUnknownFields()
		var p domain.Personality
		if err := dec.Decode(&p); err != nil {
			t.Errorf("%s 示例里的 personality 字段名对不上 domain.Personality：%v", s.Path, err)
			continue
		}
		if !p.Valid() {
			t.Errorf("%s 示例里的性格值越界：%+v", s.Path, p)
		}
	}
}

// TestDocs_接口文档接口可用 真机打一次，确认信封与字段结构正确。
func TestDocs_接口文档接口可用(t *testing.T) {
	_, srv := newApp(t, nil)
	tok := adminLogin(t, srv)

	env, data := call(t, srv, "GET", "/api/admin/api-doc", tok, nil)
	mustOK(t, env, data, "apiDoc")

	specs, ok := data["specs"].([]any)
	if !ok || len(specs) == 0 {
		t.Fatal("specs 应为非空数组")
	}
	if _, ok := data["errorCodes"]; !ok {
		t.Error("缺少 errorCodes")
	}
	if _, ok := data["conventions"]; !ok {
		t.Error("缺少 conventions")
	}
	first, _ := specs[0].(map[string]any)
	for _, k := range []string{"method", "path", "title", "group", "auth"} {
		if _, ok := first[k]; !ok {
			t.Errorf("单条规格缺字段 %q", k)
		}
	}
}

// TestDocs_未登录取不到文档 文档本身也走鉴权，不裸奔。
func TestDocs_未登录取不到文档(t *testing.T) {
	_, srv := newApp(t, nil)
	env, _ := call(t, srv, "GET", "/api/admin/api-doc", "", nil)
	if env.Code == 0 {
		t.Fatal("未登录不应取到接口文档")
	}
	if env.Code != 1002 {
		t.Fatalf("错误码应为 1002（未登录），实际 %d", env.Code)
	}
}

func hasField(fs []api.Field, name string) bool {
	for _, f := range fs {
		if f.Name == name {
			return true
		}
	}
	return false
}

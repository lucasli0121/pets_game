package config

import (
	"bufio"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// Config 运行配置。业务数值全部走管理端配置集合，这里只放进程级参数。
type Config struct {
	AppName string
	Addr    string
	Debug   bool

	MongoURI string
	MongoDB  string

	JWTSecret       string
	ClientTokenTTLH int
	AdminTokenTTLH  int
	WxAppID         string
	WxSecret        string
	AllowDevLogin   bool

	RateLimitWindowSec int
	RateLimitMaxCalls  int

	AdminInitUser string
	AdminInitPass string
	ReleaseKeep   int
	AdminDir      string

	// AssetBaseURL 静态资源基址，如 https://cdn.example.com/pets
	// 留空则配置里的相对路径原样下发，客户端只能走包内资源。
	AssetBaseURL string

	// AssetDir 静态资源根目录，由 GET /assets/ 提供。
	// 这是本地联调与单机部署用的兜底方案；生产应该把图放 CDN，
	// 把 ASSET_BASE_URL 指到 CDN，这条路由就用不上了。
	AssetDir string
}

func Load() *Config {
	loadDotEnv(".env")

	c := &Config{
		AppName:            env("APP_NAME", "宠屋养成记 服务端"),
		Addr:               env("ADDR", ":8080"),
		Debug:              envBool("DEBUG", true),
		MongoURI:           env("MONGO_URI", "mongodb://admin:admin%40123@192.168.1.63:27017/?authSource=admin"),
		MongoDB:            env("MONGO_DB", "pets_game"),
		JWTSecret:          env("JWT_SECRET", "change-me-in-production"),
		ClientTokenTTLH:    envInt("CLIENT_TOKEN_TTL_HOURS", 720),
		AdminTokenTTLH:     envInt("ADMIN_TOKEN_TTL_HOURS", 12),
		WxAppID:            env("WX_APPID", ""),
		WxSecret:           env("WX_SECRET", ""),
		AllowDevLogin:      envBool("ALLOW_DEV_LOGIN", true),
		RateLimitWindowSec: envInt("RATE_LIMIT_WINDOW_SEC", 10),
		RateLimitMaxCalls:  envInt("RATE_LIMIT_MAX_CALLS", 10),
		// 单管理员模型：只有 admin 一个账号，不做多账号/RBAC 页面（2026-10-18 决策）。
		// 默认密码 admin 仅用于本地联调，生产必须用 ADMIN_INIT_PASSWORD 覆盖。
		AdminInitUser: env("ADMIN_INIT_USERNAME", "admin"),
		AdminInitPass: env("ADMIN_INIT_PASSWORD", "admin"),
		ReleaseKeep:   envInt("CONFIG_RELEASE_KEEP", 10),
		AdminDir:      env("ADMIN_DIR", filepath.Join("internal", "web", "admin")),

		// 留空则相对路径原样下发，客户端只能走包内资源
		AssetBaseURL: strings.TrimRight(env("ASSET_BASE_URL", ""), "/"),

		// 默认 ./assets，与 bin/ 同级。目录不存在时路由仍然注册，
		// 只是所有请求都 404 —— 比启动失败好排查。
		AssetDir: env("ASSET_DIR", "assets"),
	}
	return c
}

// MongoHost 用于日志展示（隐藏凭据）。
func (c *Config) MongoHost() string {
	u, err := url.Parse(c.MongoURI)
	if err != nil || u.Host == "" {
		return "(解析失败)"
	}
	return u.Host
}

func loadDotEnv(path string) {
	f, err := os.Open(path)
	if err != nil {
		return
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		k, v, ok := strings.Cut(line, "=")
		if !ok {
			continue
		}
		k = strings.TrimSpace(k)
		v = strings.Trim(strings.TrimSpace(v), `"'`)
		if _, exists := os.LookupEnv(k); !exists {
			_ = os.Setenv(k, v)
		}
	}
}

func env(k, def string) string {
	if v, ok := os.LookupEnv(k); ok && v != "" {
		return v
	}
	return def
}

func envInt(k string, def int) int {
	if v, ok := os.LookupEnv(k); ok {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	return def
}

func envBool(k string, def bool) bool {
	if v, ok := os.LookupEnv(k); ok {
		if b, err := strconv.ParseBool(v); err == nil {
			return b
		}
	}
	return def
}

package config

import (
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"gopkg.in/yaml.v3"
)

// DefaultPath 配置文件路径，相对进程工作目录。
//
// 用 YAML 而不是 .env：.env 是隐藏文件，运维人员 ls 看不到，
// 迁移服务器时最容易漏掉，然后服务端静默用默认值跑起来 —— 排查成本极高。
const DefaultPath = "cfg/pets_server.yaml"

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
	AssetDir string
}

// fileConfig 对应 cfg/pets_server.yaml。
//
// 数值与布尔用指针：区分「配置文件里没写这一项」和「写了 0 / false」。
// 用值类型的话，没写会被当成零值，把代码里的默认值覆盖成 0。
type fileConfig struct {
	AppName string `yaml:"app_name"`
	Addr    string `yaml:"addr"`
	Debug   *bool  `yaml:"debug"`

	MongoURI string `yaml:"mongo_uri"`
	MongoDB  string `yaml:"mongo_db"`

	JWTSecret       string `yaml:"jwt_secret"`
	ClientTokenTTLH *int   `yaml:"client_token_ttl_hours"`
	AdminTokenTTLH  *int   `yaml:"admin_token_ttl_hours"`
	WxAppID         string `yaml:"wx_appid"`
	WxSecret        string `yaml:"wx_secret"`
	AllowDevLogin   *bool  `yaml:"allow_dev_login"`

	RateLimitWindowSec *int `yaml:"rate_limit_window_sec"`
	RateLimitMaxCalls  *int `yaml:"rate_limit_max_calls"`

	AdminInitUser string `yaml:"admin_init_username"`
	AdminInitPass string `yaml:"admin_init_password"`
	ReleaseKeep   *int   `yaml:"config_release_keep"`
	AdminDir      string `yaml:"admin_dir"`

	AssetBaseURL string `yaml:"asset_base_url"`
	AssetDir     string `yaml:"asset_dir"`
}

// Load 读取配置。优先级：环境变量 > YAML 文件 > 代码默认值。
//
// 环境变量优先是为了兼容容器化部署与临时覆盖：
//
//	ADDR=:9000 ./pets_server_linux
func Load() *Config {
	fc := loadYAML(DefaultPath)

	c := &Config{
		AppName: pickStr("APP_NAME", fc.AppName, "宠屋养成记 服务端"),
		Addr:    pickStr("ADDR", fc.Addr, ":8080"),
		Debug:   pickBool("DEBUG", fc.Debug, true),

		MongoURI: pickStr("MONGO_URI", fc.MongoURI, "mongodb://localhost:27017/?authSource=admin"),
		MongoDB:  pickStr("MONGO_DB", fc.MongoDB, "pets_game"),

		JWTSecret:       pickStr("JWT_SECRET", fc.JWTSecret, "change-me-in-production"),
		ClientTokenTTLH: pickInt("CLIENT_TOKEN_TTL_HOURS", fc.ClientTokenTTLH, 720),
		AdminTokenTTLH:  pickInt("ADMIN_TOKEN_TTL_HOURS", fc.AdminTokenTTLH, 12),
		WxAppID:         pickStr("WX_APPID", fc.WxAppID, ""),
		WxSecret:        pickStr("WX_SECRET", fc.WxSecret, ""),
		AllowDevLogin:   pickBool("ALLOW_DEV_LOGIN", fc.AllowDevLogin, true),

		RateLimitWindowSec: pickInt("RATE_LIMIT_WINDOW_SEC", fc.RateLimitWindowSec, 10),
		RateLimitMaxCalls:  pickInt("RATE_LIMIT_MAX_CALLS", fc.RateLimitMaxCalls, 10),

		// 单管理员模型：只有 admin 一个账号，不做多账号/RBAC 页面（2026-10-18 决策）。
		AdminInitUser: pickStr("ADMIN_INIT_USERNAME", fc.AdminInitUser, "admin"),
		AdminInitPass: pickStr("ADMIN_INIT_PASSWORD", fc.AdminInitPass, "admin"),
		ReleaseKeep:   pickInt("CONFIG_RELEASE_KEEP", fc.ReleaseKeep, 10),
		AdminDir:      pickStr("ADMIN_DIR", fc.AdminDir, filepath.Join("internal", "web", "admin")),

		AssetBaseURL: strings.TrimRight(pickStr("ASSET_BASE_URL", fc.AssetBaseURL, ""), "/"),
		AssetDir:     pickStr("ASSET_DIR", fc.AssetDir, "assets"),
	}
	return c
}

// loadYAML 读配置文件。文件不存在时返回零值，全部走默认 —— 
// 不报错是有意的：本地开发经常不写配置文件，直接跑默认值就行。
func loadYAML(path string) fileConfig {
	var fc fileConfig
	b, err := os.ReadFile(path)
	if err != nil {
		return fc
	}
	// 解析失败要静默还是报错？这里选静默，但把错误打到 stderr，
	// 否则配置写错一个缩进就变成「配置没生效」而且毫无线索。
	if err := yaml.Unmarshal(b, &fc); err != nil {
		os.Stderr.WriteString("[config] 解析 " + path + " 失败: " + err.Error() + "，将使用默认值\n")
		return fileConfig{}
	}
	return fc
}

func pickStr(envKey, yamlVal, def string) string {
	if v, ok := os.LookupEnv(envKey); ok && v != "" {
		return v
	}
	if yamlVal != "" {
		return yamlVal
	}
	return def
}

func pickInt(envKey string, yamlVal *int, def int) int {
	if v, ok := os.LookupEnv(envKey); ok {
		if n, err := strconv.Atoi(v); err == nil {
			return n
		}
	}
	if yamlVal != nil {
		return *yamlVal
	}
	return def
}

func pickBool(envKey string, yamlVal *bool, def bool) bool {
	if v, ok := os.LookupEnv(envKey); ok {
		if b, err := strconv.ParseBool(v); err == nil {
			return b
		}
	}
	if yamlVal != nil {
		return *yamlVal
	}
	return def
}

// MongoHost 用于日志展示（隐藏凭据）。
func (c *Config) MongoHost() string {
	u, err := url.Parse(c.MongoURI)
	if err != nil || u.Host == "" {
		return "(解析失败)"
	}
	return u.Host
}

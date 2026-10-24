package api

import (
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
)

// assetHandler 提供 /assets/ 下的静态资源（场景背景图、缩略图）。
//
// 存在的理由：本地联调与单机部署没有 CDN，但客户端要验证「从后台下载场景图
// + 本地缓存」这条链路。生产环境把图丢 CDN、把 ASSET_BASE_URL 指过去即可，
// 这条路由自然就不会被访问。
//
// 三条约束：
//  1. 只放图片。白名单而不是黑名单 —— 这个目录以后可能被运营塞进别的东西，
//     无脑放行整个目录等于把服务器文件系统开了个口子。
//  2. 目录不存在时返回 404 而不是启动报错：漏建 assets/ 是常见疏忽，
//     不该让整个服务起不来。
//  3. 加 Cache-Control，客户端与中间代理都别反复拉同一张图。
func assetHandler(root string) http.Handler {
	fs := http.FileServer(http.Dir(root))
	absRoot, _ := filepath.Abs(root)

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// http.FileServer 自己会清理 ..，这里再挡一层是因为
		// 早期版本对 Windows 反斜杠的清理不彻底。
		clean := path.Clean("/" + strings.TrimPrefix(r.URL.Path, "/"))
		if strings.Contains(clean, "..") {
			http.NotFound(w, r)
			return
		}

		switch strings.ToLower(path.Ext(clean)) {
		case ".jpg", ".jpeg", ".png", ".webp":
		default:
			http.NotFound(w, r)
			return
		}

		// 目录整个不存在时给一条明确的日志，比让调用方对着 404 猜要好
		if absRoot != "" {
			if st, err := os.Stat(filepath.Join(absRoot, filepath.FromSlash(clean))); err != nil || st.IsDir() {
				http.NotFound(w, r)
				return
			}
		}

		w.Header().Set("Cache-Control", "public, max-age=86400")
		fs.ServeHTTP(w, r)
	})
}

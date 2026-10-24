package web

import (
	"embed"
	"io/fs"
	"net/http"
	"os"
)

// admin 管理端单页应用打进二进制，部署只需一个 exe。
//
//go:embed admin
var adminFS embed.FS

// AdminHandler 优先用磁盘目录（改前端不用重编译），
// 磁盘目录不存在时回落到内嵌资源（发布形态）。
func AdminHandler(diskDir string) http.Handler {
	if diskDir != "" {
		if st, err := os.Stat(diskDir); err == nil && st.IsDir() {
			return http.FileServer(http.Dir(diskDir))
		}
	}
	sub, err := fs.Sub(adminFS, "admin")
	if err != nil {
		return http.NotFoundHandler()
	}
	return http.FileServer(http.FS(sub))
}

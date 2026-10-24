package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"pets_server/internal/app"
	"pets_server/internal/config"
)

func main() {
	cfg := config.Load()
	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	log.SetPrefix("[pets] ")

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	a, err := app.Build(ctx, cfg)
	if err != nil {
		log.Fatalf("启动失败: %v", err)
	}
	defer a.Close()
	log.Printf("已连接 MongoDB：%s / db=%s", cfg.MongoHost(), cfg.MongoDB)

	srv := &http.Server{
		Addr:              cfg.Addr,
		Handler:           a.Handler,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      20 * time.Second,
		IdleTimeout:       60 * time.Second,
	}

	go func() {
		log.Printf("HTTP 监听 %s，管理端 http://127.0.0.1%s/admin/", cfg.Addr, cfg.Addr)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("HTTP 启动失败: %v", err)
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	<-stop

	shutdownCtx, c := context.WithTimeout(context.Background(), 10*time.Second)
	defer c()
	log.Println("正在关闭…")
	_ = srv.Shutdown(shutdownCtx)
}

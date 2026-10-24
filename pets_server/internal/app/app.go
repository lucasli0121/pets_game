package app

import (
	"context"
	"net/http"

	"pets_server/internal/api"
	"pets_server/internal/config"
	"pets_server/internal/service"
	"pets_server/internal/store"
)

// App 装配好的应用。main 与端到端测试共用同一套装配，避免两边行为不一致。
type App struct {
	Cfg     *config.Config
	DB      *store.DB
	Handler http.Handler

	// API 暴露 Handlers，供一致性测试读取已注册的路由表。
	API *api.Handlers

	CfgSvc  *service.ConfigService
	Assets  *service.AssetsService
	Pets    *service.PetService
	Shop    *service.ShopService
	CheckIn *service.CheckInService
	Ads     *service.AdService
	Admin   *service.AdminService
	Idem    *service.IdemService
}

func Build(ctx context.Context, cfg *config.Config) (*App, error) {
	db, err := store.Open(ctx, cfg.MongoURI, cfg.MongoDB)
	if err != nil {
		return nil, err
	}

	cfgSvc := service.NewConfigService(db, cfg.ReleaseKeep, cfg.AssetBaseURL)
	assets := service.NewAssetsService(db)
	idem := service.NewIdemService(db)
	// checkin 要在 pets 之前建：sync-state 要下发签到状态（今天签没签、连续几天）
	checkin := service.NewCheckInService(db, cfgSvc, assets)
	pets := service.NewPetService(db, cfgSvc, assets, checkin)
	shop := service.NewShopService(db, cfgSvc, assets)
	ads := service.NewAdService(db, cfgSvc, assets)
	admin := service.NewAdminService(db, cfgSvc, assets, cfg.JWTSecret, cfg.AdminTokenTTLH)

	if _, _, err := service.Seed(ctx, db, cfgSvc, cfg.AdminInitUser, cfg.AdminInitPass); err != nil {
		return nil, err
	}

	h := api.New(cfg, db, cfgSvc, assets, idem, pets, shop, checkin, ads, admin)

	return &App{
		Cfg: cfg, DB: db, Handler: h.Routes(), API: h,
		CfgSvc: cfgSvc, Assets: assets, Pets: pets, Shop: shop,
		CheckIn: checkin, Ads: ads, Admin: admin, Idem: idem,
	}, nil
}

func (a *App) Close() {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	_ = a.DB.Close(ctx)
}

package service

import (
	"context"
	"encoding/json"

	"go.mongodb.org/mongo-driver/v2/bson"
	"go.mongodb.org/mongo-driver/v2/mongo/options"

	"pets_server/internal/auth"
	"pets_server/internal/domain"
	"pets_server/internal/store"
	"pets_server/internal/timeutil"
)

// InitDefault 首次启动灌入默认配置（文档 9 节：导入初始配置，config_version=1）。
func (s *ConfigService) InitDefault(ctx context.Context, snap *domain.ConfigSnapshot, operator string) error {
	if err := s.writeLists(ctx, snap); err != nil {
		return err
	}
	now := timeutil.NowMs()
	meta := domain.ConfigMeta{
		ID: singletonID, Version: 1, UpdatedAt: now,
		UpdatedBy: operator, Note: "系统初始化默认配置",
	}
	if _, err := s.db.C(store.CollConfigMeta).ReplaceOne(ctx,
		store.F("_id", singletonID), meta, options.Replace().SetUpsert(true)); err != nil {
		return err
	}
	payload, _ := json.Marshal(snap)
	_, err := s.db.C(store.CollConfigRelease).InsertOne(ctx, domain.ConfigRelease{
		ID: newID(), Version: 1, Payload: string(payload), Diff: "[]",
		Note: "系统初始化默认配置", PublishedBy: operator, CreatedAt: now,
	})
	if err != nil && !store.IsDuplicate(err) {
		return err
	}
	s.Invalidate()
	return nil
}

// Seed 首次启动初始化：配置 + 管理员账号。已存在则跳过，可重复执行。
func Seed(ctx context.Context, db *store.DB, cfgSvc *ConfigService, adminUser, adminPass string) (configSeeded, adminSeeded bool, err error) {
	n, err := db.C(store.CollConfigMeta).CountDocuments(ctx, bson.M{})
	if err != nil {
		return false, false, err
	}
	if n == 0 {
		snap := domain.DefaultSnapshot()
		if err := cfgSvc.InitDefault(ctx, &snap, "system"); err != nil {
			return false, false, err
		}
		configSeeded = true
	}

	ac, err := db.C(store.CollAdminUsers).CountDocuments(ctx, bson.M{})
	if err != nil {
		return configSeeded, false, err
	}
	if ac == 0 {
		hash, err := auth.HashPassword(adminPass)
		if err != nil {
			return configSeeded, false, err
		}
		_, err = db.C(store.CollAdminUsers).InsertOne(ctx, domain.AdminUser{
			ID: newID(), Username: adminUser, PasswordHash: hash,
			Role: domain.RoleSuper, Status: "active", CreatedAt: timeutil.NowMs(),
		})
		if err != nil {
			return configSeeded, false, err
		}
		adminSeeded = true
	}
	return configSeeded, adminSeeded, nil
}

"""运行配置。所有可变数值走环境变量或管理端配置表，不写死在业务代码里。"""
from __future__ import annotations

from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

BASE_DIR = Path(__file__).resolve().parent.parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=str(BASE_DIR / ".env"),
        env_file_encoding="utf-8",
        extra="ignore",
    )

    # ---- 基础 ----
    app_name: str = "宠屋养成记 服务端"
    debug: bool = True
    # 开发用 SQLite；生产改成 postgresql+psycopg://user:pass@host:5432/pets
    database_url: str = f"sqlite:///{(BASE_DIR / 'data' / 'pets.db').as_posix()}"
    sql_echo: bool = False

    # ---- 鉴权 ----
    jwt_secret: str = "change-me-in-production"
    jwt_alg: str = "HS256"
    client_token_ttl_hours: int = 24 * 30
    admin_token_ttl_hours: int = 12

    # ---- 微信小游戏 ----
    wx_appid: str = ""
    wx_secret: str = ""
    # 未配置 wx_appid/secret 且 debug=True 时，允许客户端直接传 dev_openid 登录
    allow_dev_login: bool = True

    # ---- 安全频控（文档 6.6）----
    rate_limit_window_sec: int = 10
    rate_limit_max_calls: int = 10

    # ---- 管理端初始账号 ----
    admin_init_username: str = "admin"
    admin_init_password: str = "admin123456"

    # ---- 部署 ----
    config_release_keep: int = 10  # 文档 7.2：保留最近 10 个版本


settings = Settings()

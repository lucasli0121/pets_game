"""时间工具：所有自然日按 UTC+8 切分（文档 5 节统一约定）。"""
from __future__ import annotations

from datetime import date, datetime, timedelta, timezone

CST = timezone(timedelta(hours=8), "Asia/Shanghai")


def now_ms() -> int:
    """当前毫秒时间戳。"""
    return int(datetime.now(tz=CST).timestamp() * 1000)


def ms_to_dt(ms: int) -> datetime:
    return datetime.fromtimestamp(ms / 1000, tz=CST)


def dt_to_ms(dt: datetime) -> int:
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=CST)
    return int(dt.timestamp() * 1000)


def cst_today(ms: int | None = None) -> str:
    """UTC+8 自然日，YYYY-MM-DD。"""
    ts = now_ms() if ms is None else ms
    return ms_to_dt(ts).strftime("%Y-%m-%d")


def cst_date(ms: int) -> date:
    return ms_to_dt(ms).date()


def hours_between(later_ms: int, earlier_ms: int) -> int:
    """整小时数，向下取整。"""
    if later_ms <= earlier_ms:
        return 0
    return (later_ms - earlier_ms) // 3_600_000

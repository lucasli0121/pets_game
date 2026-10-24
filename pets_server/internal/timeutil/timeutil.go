package timeutil

import "time"

// CST UTC+8。文档要求所有自然日按 UTC+8 切分，不随服务器时区变化。
var CST = time.FixedZone("Asia/Shanghai", 8*3600)

// NowMs 当前毫秒时间戳。
func NowMs() int64 { return time.Now().In(CST).UnixMilli() }

// Today UTC+8 自然日，YYYY-MM-DD。
func Today() string { return TodayAt(NowMs()) }

func TodayAt(ms int64) string {
	return time.UnixMilli(ms).In(CST).Format("2006-01-02")
}

// DayStart 返回某毫秒时间所属 UTC+8 自然日的 00:00 毫秒时间戳。
func DayStart(ms int64) int64 {
	t := time.UnixMilli(ms).In(CST)
	return time.Date(t.Year(), t.Month(), t.Day(), 0, 0, 0, 0, CST).UnixMilli()
}

// Format 统一的时间展示格式（管理端用）。
func Format(ms int64) string {
	if ms <= 0 {
		return ""
	}
	return time.UnixMilli(ms).In(CST).Format("2006-01-02 15:04:05")
}

// ParseDateMs 把 YYYY-MM-DD 解析为该日 UTC+8 00:00 的毫秒时间戳。
func ParseDateMs(date string) (int64, error) {
	t, err := time.ParseInLocation("2006-01-02", date, CST)
	if err != nil {
		return 0, err
	}
	return t.UnixMilli(), nil
}

package domain

// 状态衰减（文档 6.4）
//
// 服务端只在 login / syncState 时按时间差结算，不起定时任务。
// delta_hours = floor((now - last_settle_at) / 3600000)，按整小时扣。
// 离线超过 maxHours（24）按 24 小时封顶，避免长期不登录回来状态全空。

// MinStateValue 养成状态（饱腹 / 快乐）的下限。
//
// 产品决策（2026-10-18）：状态可以掉到很低，但不能归零。
// 原先按 [0,100] 夹取，饱腹 −10/h 意味着离线 10 小时就归零，
// 「48 小时不登录状态不空」这条验收标准因此不可能达到。
// 现改为下限 1：衰减与加值一律夹在 [1, 100]，状态只会在低位徘徊，不会归零。
const MinStateValue = 1

type DecayParams struct {
	SatietyPerHour   int // 10
	HappinessPerHour int // 5
	MaxHours         int // 24
	// MaxState 饱腹/快乐的数值上限，0 表示按 100。
	// 加值路径（喂食/互动）与这里的衰减结算必须用同一个上限，见 PointsConfig.StateCap。
	MaxState int // 100
}

type DecayResult struct {
	Satiety   int
	Happiness int
	Hours     int   // 实际扣减的小时数（已封顶）
	SettledAt int64 // 结算后应写入的 last_settle_at
}

// Settle 计算衰减。
//
// SettledAt 只在「整小时」上推进，剩余不足 1 小时的时间留到下次结算，
// 否则每次登录都以毫秒级精度重置基准，用户永远攒不满 1 小时。
func Settle(satiety, happiness int, lastSettleAt, now int64, p DecayParams) DecayResult {
	const hourMS = 3_600_000
	res := DecayResult{Satiety: satiety, Happiness: happiness, SettledAt: lastSettleAt}

	if lastSettleAt <= 0 {
		res.SettledAt = now
		return res
	}
	if now <= lastSettleAt {
		return res
	}

	hours := int((now - lastSettleAt) / hourMS)
	if hours <= 0 {
		return res
	}
	if p.MaxHours > 0 && hours > p.MaxHours {
		hours = p.MaxHours
	}

	res.Hours = hours
	maxState := p.MaxState
	if maxState <= 0 {
		maxState = 100 // 老调用方没传时保持原行为
	}
	res.Satiety = clampRange(satiety-p.SatietyPerHour*hours, MinStateValue, maxState)
	res.Happiness = clampRange(happiness-p.HappinessPerHour*hours, MinStateValue, maxState)
	res.SettledAt = lastSettleAt + int64(hours)*hourMS
	return res
}

func clamp0(v, max int) int {
	if v < 0 {
		return 0
	}
	if max > 0 && v > max {
		return max
	}
	return v
}

// clampRange 夹到 [min, max]；max 传 0 表示不设上限。
func clampRange(v, min, max int) int {
	if v < min {
		return min
	}
	if max > 0 && v > max {
		return max
	}
	return v
}

// Clamp 夹到 [0, max]，用于积分等「可以归零」的字段。
func Clamp(v, max int) int { return clamp0(v, max) }

// ClampState 把养成状态值夹到 [MinStateValue, max]。
// 喂养、互动等加值路径也必须走这里，否则数值为 0 的历史记录
// 会因为「加 0 仍然是 0」而一直停留在下限之下。
func ClampState(v, max int) int { return clampRange(v, MinStateValue, max) }

// SatietyState / HappinessState 状态标签（PRD 4.3 阈值）
func SatietyState(satiety int, p PointsConfig) string {
	switch {
	case satiety < p.SatietyExhausted:
		return "exhausted" // 饥饿 + 疲惫
	case satiety < p.SatietyHungry:
		return "hungry" // 饥饿
	default:
		return "normal"
	}
}

func HappinessState(happiness int, p PointsConfig) string {
	switch {
	case happiness < p.HappinessDepressed:
		return "depressed" // 沮丧
	case happiness < p.HappinessSad:
		return "sad" // 难过
	default:
		return "normal"
	}
}

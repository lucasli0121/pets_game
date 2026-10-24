package domain

import "sort"

// LevelCurve 等级曲线，由 config_level 构造成内存结构。
// 服务端只读表、不写死公式（文档 13.8 ①）。
type LevelCurve struct {
	items []LevelItem // 按 level 升序，已过滤 enabled=false
}

func NewLevelCurve(items []LevelItem) *LevelCurve {
	cp := make([]LevelItem, 0, len(items))
	for _, it := range items {
		if it.Level >= 1 && it.Enabled && it.ExpRequired > 0 {
			cp = append(cp, it)
		}
	}
	sort.Slice(cp, func(i, j int) bool { return cp[i].Level < cp[j].Level })
	return &LevelCurve{items: cp}
}

func (c *LevelCurve) Empty() bool { return c == nil || len(c.items) == 0 }

// MaxLevel 曲线最高等级。
func (c *LevelCurve) MaxLevel() int {
	if c.Empty() {
		return 1
	}
	return c.items[len(c.items)-1].Level
}

// Resolve 读累计经验，逐级扣减直到不足，得到等级与级内进度（文档 13.7 ②）。
func (c *LevelCurve) Resolve(totalExp int64) (level int, expInLevel, expRequired int64) {
	if c.Empty() {
		return 1, totalExp, 0
	}
	level = c.items[0].Level
	remain := totalExp
	for i := range c.items {
		if remain < c.items[i].ExpRequired {
			return c.items[i].Level, remain, c.items[i].ExpRequired
		}
		remain -= c.items[i].ExpRequired
		level = c.items[i].Level
	}
	// 已到顶：不再升级，级内进度按满算
	last := c.items[len(c.items)-1]
	return last.Level, 0, 0
}

// RewardFor 升到某一级发放的积分。表里没配则回落到 config_points.levelup_reward。
func (c *LevelCurve) RewardFor(level int, fallback int) int {
	if c.Empty() {
		return fallback
	}
	for _, it := range c.items {
		if it.Level == level {
			if it.RewardPoints > 0 {
				return it.RewardPoints
			}
			return fallback
		}
	}
	return fallback
}

// ScaleFor 等级对应的体型缩放（客户端用）。
func (c *LevelCurve) ScaleFor(level int) float64 {
	if c.Empty() {
		return 1
	}
	for _, it := range c.items {
		if it.Level == level {
			if it.Scale > 0 {
				return it.Scale
			}
			return 1
		}
	}
	return 1
}

// LevelsBetween 返回 (from, to] 之间所有等级，用于一次加很多经验时逐级发奖。
func (c *LevelCurve) LevelsBetween(fromLevel, toLevel int) []int {
	var out []int
	for lv := fromLevel + 1; lv <= toLevel; lv++ {
		out = append(out, lv)
	}
	return out
}

package engine

import (
	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"math"
)

type NativeQuality struct {
	Source                    string  `json:"source"`
	Complete                  bool    `json:"complete"`
	Samples                   int     `json:"samples"`
	MinShieldCoverage         float64 `json:"minShieldCoverage"`
	MinCriticalShieldCoverage float64 `json:"minCriticalShieldCoverage"`
	MaxFailedRounds           int     `json:"maxFailedRounds"`
	MaxDamageGapSeconds       float64 `json:"maxDamageGapSeconds"`
	MinPartyHP                float64 `json:"minPartyHp"`
}
type qualityObserver struct {
	flow                                                      *nativeflow.Evaluator
	frames, shielded, critical, protected, lastDamage, maxGap int
	minHP                                                     float64
}

func observeNativeQuality(c *core.Core, flow *nativeflow.Evaluator) *qualityObserver {
	q := &qualityObserver{flow: flow, minHP: 1}
	c.Events.Subscribe(event.OnEnemyDamage, func(args ...any) {
		if damage, ok := args[2].(float64); ok && damage > 0 {
			q.lastDamage = c.F
		}
	}, "bettergi/native-quality-damage")
	c.Events.Subscribe(event.OnTick, func(...any) {
		q.frames++
		active := c.Player.ActiveChar().Index()
		shielded := c.Player.Shields.CharacterIsShielded(active, active)
		if shielded {
			q.shielded++
		}
		if flow.CriticalOutputActive() {
			q.critical++
			if shielded {
				q.protected++
			}
		}
		q.maxGap = max(q.maxGap, c.F-q.lastDamage)
		for _, actor := range c.Player.Chars() {
			q.minHP = math.Min(q.minHP, actor.CurrentHPRatio())
		}
	}, "bettergi/native-quality-tick")
	return q
}
func (q *qualityObserver) finish() NativeQuality {
	v := NativeQuality{Source: "gcsim.sdk.shields/actions/hp", Complete: q.frames > 0 && q.critical > 0, Samples: 1, MaxFailedRounds: q.flow.FailedRounds, MaxDamageGapSeconds: float64(q.maxGap) / 60, MinPartyHP: q.minHP}
	if q.frames > 0 {
		v.MinShieldCoverage = float64(q.shielded) / float64(q.frames)
	}
	if q.critical > 0 {
		v.MinCriticalShieldCoverage = float64(q.protected) / float64(q.critical)
	}
	return v
}
func mergeNativeQuality(report *Report, v NativeQuality) {
	if report.NativeQuality == nil {
		report.NativeQuality = &v
		return
	}
	q := report.NativeQuality
	q.Complete = q.Complete && v.Complete
	q.Samples += v.Samples
	q.MinShieldCoverage = math.Min(q.MinShieldCoverage, v.MinShieldCoverage)
	q.MinCriticalShieldCoverage = math.Min(q.MinCriticalShieldCoverage, v.MinCriticalShieldCoverage)
	q.MaxFailedRounds = max(q.MaxFailedRounds, v.MaxFailedRounds)
	q.MaxDamageGapSeconds = math.Max(q.MaxDamageGapSeconds, v.MaxDamageGapSeconds)
	q.MinPartyHP = math.Min(q.MinPartyHP, v.MinPartyHP)
}

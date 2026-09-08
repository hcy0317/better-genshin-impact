package engine

import (
	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/event"
)

type EnergyObservation struct {
	Round    string             `json:"round"`
	Frame    int                `json:"frame"`
	Boundary string             `json:"boundary"`
	Phase    string             `json:"phase"`
	Energy   map[string]float64 `json:"energy"`
}

func observeRoundEnergy(c *core.Core, rounds []Round) *[]EnergyObservation {
	observations := make([]EnergyObservation, 0, len(rounds)*2)
	capture := func(round Round, boundary string) {
		values := make(map[string]float64)
		for _, character := range c.Player.Chars() {
			values[character.Base.Key.String()] = character.Energy
		}
		observations = append(observations, EnergyObservation{Round: round.ID, Frame: c.F, Boundary: boundary, Phase: "on_tick_before_action", Energy: values})
	}
	for _, round := range rounds {
		if round.StartFrame == 0 {
			capture(round, "start")
		}
	}
	c.Events.Subscribe(event.OnTick, func(args ...any) {
		for _, round := range rounds {
			if c.F == round.StartFrame && c.F != 0 {
				capture(round, "start")
			}
			if c.F == round.EndFrame {
				capture(round, "end")
			}
		}
	}, "bettergi/round-energy")
	return &observations
}

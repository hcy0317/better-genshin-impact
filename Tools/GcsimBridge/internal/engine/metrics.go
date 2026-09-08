package engine

import (
	"errors"
	"github.com/genshinsim/gcsim/pkg/stats"
	"math"
	"sort"
)

type Metric struct {
	Character   string  `json:"character"`
	Kind        string  `json:"kind"`
	Value       float64 `json:"value"`
	Unit        string  `json:"unit"`
	Aggregation string  `json:"aggregation"`
	Source      string  `json:"source"`
}

func insideWindows(frame int, rounds []Round) bool {
	if len(rounds) == 0 {
		return true
	}
	for _, round := range rounds {
		if frame >= round.StartFrame && frame < round.EndFrame {
			return true
		}
	}
	return false
}

func SummarizeMetrics(samples []stats.Result, rounds []Round) ([]Metric, error) {
	if len(samples) == 0 || (len(rounds) > 0 && validateRounds(rounds, 0) != nil) {
		return nil, errors.New("cannot aggregate an empty/incomplete metric batch")
	}
	names := make([]string, 0, len(samples[0].Characters))
	for _, character := range samples[0].Characters {
		names = append(names, character.Name)
	}
	sort.Strings(names)
	if len(names) == 0 {
		return nil, errors.New("missing character metrics")
	}
	for i, name := range names {
		if name == "" || (i > 0 && names[i-1] == name) {
			return nil, errors.New("invalid character metric identity")
		}
	}
	damage, healing := make(map[string]float64), make(map[string]float64)
	divisor := float64(len(samples) * max(1, len(rounds)))
	for _, sample := range samples {
		for _, round := range rounds {
			if sample.Duration < round.EndFrame {
				return nil, errors.New("incomplete metric window")
			}
		}
		if len(sample.Characters) != len(names) {
			return nil, errors.New("missing character metrics in a sample")
		}
		seen := make(map[string]bool)
		for _, character := range sample.Characters {
			i := sort.SearchStrings(names, character.Name)
			if i == len(names) || names[i] != character.Name || seen[character.Name] {
				return nil, errors.New("inconsistent character metric identity")
			}
			seen[character.Name] = true
			d, h := 0.0, 0.0
			for _, hit := range character.DamageEvents {
				if math.IsNaN(hit.Damage) || math.IsInf(hit.Damage, 0) || hit.Damage < 0 {
					return nil, errors.New("invalid raw damage metric")
				}
				if insideWindows(hit.Frame, rounds) {
					d += hit.Damage
				}
			}
			for _, heal := range character.HealEvents {
				if math.IsNaN(heal.Heal) || math.IsInf(heal.Heal, 0) || heal.Heal < 0 {
					return nil, errors.New("invalid raw healing metric")
				}
				if insideWindows(heal.Frame, rounds) {
					h += heal.Heal
				}
			}
			damage[character.Name] += d / divisor
			healing[character.Name] += h / divisor
		}
	}
	metrics := make([]Metric, 0, len(names)*2)
	for _, name := range names {
		metrics = append(metrics, Metric{Character: name, Kind: "damage_per_round", Value: damage[name], Unit: "damage/round", Aggregation: "arithmetic_mean_before_normalization", Source: "gcsim.DamageEvents"})
		metrics = append(metrics, Metric{Character: name, Kind: "effective_healing_per_round", Value: healing[name], Unit: "hp/round", Aggregation: "arithmetic_mean_before_normalization", Source: "gcsim.HealEvents"})
	}
	return metrics, nil
}

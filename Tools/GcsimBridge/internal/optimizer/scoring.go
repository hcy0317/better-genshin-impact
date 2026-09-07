package optimizer

import (
	"errors"
	"math"
	"sort"
)

type Target struct {
	Scenario  string  `json:"scenario"`
	Metric    string  `json:"metric"`
	Weight    float64 `json:"weight"`
	Reference float64 `json:"reference"`
}

type CharacterGoal struct {
	Character string   `json:"character"`
	Weight    float64  `json:"weight"`
	Targets   []Target `json:"targets"`
}

type ScenarioScore struct {
	DPS     float64                       `json:"dps"`
	Metrics map[string]map[string]float64 `json:"metrics"`
}

type Rank struct {
	Balanced      float64   `json:"weightedDps"`
	Peak          float64   `json:"targetRetention"`
	PeakAvailable bool      `json:"targetRetentionAvailable"`
	Shortfall     []float64 `json:"weightedShortfall"`
	Changes       int       `json:"changes"`
	Key           string    `json:"key"`
}

func Score(mode string, weights map[string]float64, results map[string]ScenarioScore, goals []CharacterGoal) (Rank, error) {
	if mode != "balanced" && mode != "peak" && mode != "fallback" {
		return Rank{}, errors.New("unknown optimization mode")
	}
	rank := Rank{PeakAvailable: true}
	totalWeight := 0.0
	keys := make([]string, 0, len(weights))
	for key := range weights {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	for _, key := range keys {
		weight := weights[key]
		value, ok := results[key]
		if !ok || !finite(weight) || weight < 0 || !finite(value.DPS) || value.DPS < 0 {
			return Rank{}, errors.New("invalid or missing scenario score/weight")
		}
		totalWeight += weight
		rank.Balanced += weight * value.DPS
	}
	if totalWeight <= 0 || !finite(totalWeight) || !finite(rank.Balanced) {
		return Rank{}, errors.New("at least one finite positive scenario weight is required")
	}
	characterSum, characterMax := 0.0, 0.0
	for _, goal := range goals {
		if !finite(goal.Weight) || goal.Weight < 0 {
			return Rank{}, errors.New("invalid character weight")
		}
		characterSum += goal.Weight
		characterMax = max(characterMax, goal.Weight)
	}
	if !finite(characterSum) {
		return Rank{}, errors.New("character weight sum overflow")
	}
	if characterSum == 0 {
		rank.PeakAvailable = false
		if mode != "balanced" {
			return Rank{}, errors.New("positive character goals are required")
		}
		return rank, nil
	}
	for _, goal := range goals {
		if goal.Weight == 0 {
			continue
		}
		targetSum, targetMax := 0.0, 0.0
		for _, target := range goal.Targets {
			if !finite(target.Weight) || target.Weight < 0 {
				return Rank{}, errors.New("invalid Build weight")
			}
			targetSum += target.Weight
			targetMax = max(targetMax, target.Weight)
		}
		if !finite(targetSum) {
			return Rank{}, errors.New("Build weight sum overflow")
		}
		if targetSum == 0 {
			rank.PeakAvailable = false
			continue
		}
		for _, target := range goal.Targets {
			if target.Weight == 0 {
				continue
			}
			result, ok := results[target.Scenario]
			value, available := result.Metrics[goal.Character][target.Metric]
			if !ok || !available || !finite(value) || !finite(target.Reference) || target.Reference <= 0 {
				rank.PeakAvailable = false
				continue
			}
			u := min(1, max(0, value/target.Reference))
			rank.Peak += (goal.Weight / characterSum) * (target.Weight / targetSum) * u
			rank.Shortfall = append(rank.Shortfall, (goal.Weight/characterMax)*(target.Weight/targetMax)*(1-u))
		}
	}
	if !rank.PeakAvailable {
		rank.Peak = 0
		rank.Shortfall = nil
		if mode != "balanced" {
			return Rank{}, errors.New("required role metrics/reference values are unavailable")
		}
	}
	sort.Sort(sort.Reverse(sort.Float64Slice(rank.Shortfall)))
	return rank, nil
}

// Better compares already qualified candidates within one immutable task.
func Better(mode string, left, right Rank) bool {
	switch mode {
	case "balanced":
		if left.Balanced != right.Balanced {
			return left.Balanced > right.Balanced
		}
		if left.PeakAvailable && right.PeakAvailable && left.Peak != right.Peak {
			return left.Peak > right.Peak
		}
	case "peak":
		if left.Peak != right.Peak {
			return left.Peak > right.Peak
		}
		if left.Balanced != right.Balanced {
			return left.Balanced > right.Balanced
		}
	case "fallback":
		for i := 0; i < min(len(left.Shortfall), len(right.Shortfall)); i++ {
			if left.Shortfall[i] != right.Shortfall[i] {
				return left.Shortfall[i] < right.Shortfall[i]
			}
		}
		if left.Peak != right.Peak {
			return left.Peak > right.Peak
		}
		if left.Balanced != right.Balanced {
			return left.Balanced > right.Balanced
		}
	default:
		return false
	}
	if left.Changes != right.Changes {
		return left.Changes < right.Changes
	}
	return left.Key < right.Key
}

func finite(value float64) bool { return !math.IsNaN(value) && !math.IsInf(value, 0) }

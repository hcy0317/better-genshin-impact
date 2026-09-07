package optimizer_test

import (
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
	"testing"
)

func TestBalancedMaximizesActualDPS(t *testing.T) {
	weights := map[string]float64{"a": 1, "b": 1}
	a, err := optimizer.Score("balanced", weights, map[string]optimizer.ScenarioScore{"a": {DPS: 100}, "b": {DPS: 50}}, nil)
	if err != nil {
		t.Fatal(err)
	}
	b, err := optimizer.Score("balanced", weights, map[string]optimizer.ScenarioScore{"a": {DPS: 74}, "b": {DPS: 74}}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if a.Balanced != 150 || b.Balanced != 148 || !optimizer.Better("balanced", a, b) {
		t.Fatalf("balanced must select 150 over 148: %+v %+v", a, b)
	}
}

func TestPeakWeightsAndCompleteFallbackVector(t *testing.T) {
	weights := map[string]float64{"team": 1}
	goals := []optimizer.CharacterGoal{{Character: "a", Weight: 4, Targets: []optimizer.Target{{Scenario: "team", Metric: "damage", Weight: 1, Reference: 1}}}, {Character: "b", Weight: 1, Targets: []optimizer.Target{{Scenario: "team", Metric: "damage", Weight: 1, Reference: 1}}}}
	makeRank := func(mode string, values map[string]map[string]float64) optimizer.Rank {
		rank, err := optimizer.Score(mode, weights, map[string]optimizer.ScenarioScore{"team": {DPS: 100, Metrics: values}}, goals)
		if err != nil {
			t.Fatal(err)
		}
		return rank
	}
	a := makeRank("peak", map[string]map[string]float64{"a": {"damage": 0.95}, "b": {"damage": 0.7}})
	b := makeRank("peak", map[string]map[string]float64{"a": {"damage": 0.8}, "b": {"damage": 0.9}})
	if !optimizer.Better("peak", a, b) {
		t.Fatalf("4:1 character weights did not protect a: %+v %+v", a, b)
	}
	a = optimizer.Rank{Shortfall: []float64{.4, .3, 0}, Peak: .7667, Balanced: 200}
	b = optimizer.Rank{Shortfall: []float64{.4, .2, .2}, Peak: .7333, Balanced: 100}
	if !optimizer.Better("fallback", b, a) {
		t.Fatal("fallback skipped the second shortage component")
	}
}

func TestBalancedIgnoresReferencesAndRejectsBadWeights(t *testing.T) {
	weights := map[string]float64{"a": 1, "b": 1}
	goals := []optimizer.CharacterGoal{{Character: "a", Weight: 1, Targets: []optimizer.Target{{Scenario: "a", Metric: "unknown", Reference: 0, Weight: 1}}}}
	rank, err := optimizer.Score("balanced", weights, map[string]optimizer.ScenarioScore{"a": {DPS: 140}, "b": {DPS: 35}}, goals)
	if err != nil || rank.Balanced != 175 || rank.PeakAvailable {
		t.Fatalf("nonrequired reference blocked DPS: %+v %v", rank, err)
	}
	if _, err := optimizer.Score("balanced", map[string]float64{"a": 0}, map[string]optimizer.ScenarioScore{"a": {DPS: 100}}, nil); err == nil {
		t.Fatal("zero objective accepted")
	}
}

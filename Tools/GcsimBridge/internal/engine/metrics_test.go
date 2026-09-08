package engine_test

import (
	"github.com/genshinsim/gcsim/pkg/stats"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"testing"
)

func TestRawDamageIsAveragedBeforeAnyReferenceOrCap(t *testing.T) {
	samples := []stats.Result{
		{Seed: 1, Duration: 100, Characters: []stats.CharacterResult{{Name: "amber", DamageEvents: []stats.DamageEvent{{Frame: 10, Damage: 0}}}}},
		{Seed: 2, Duration: 100, Characters: []stats.CharacterResult{{Name: "amber", DamageEvents: []stats.DamageEvent{{Frame: 10, Damage: 200}}}}},
	}
	metrics, err := engine.SummarizeMetrics(samples, []engine.Round{{ID: "cycle", StartFrame: 0, EndFrame: 100}})
	if err != nil {
		t.Fatal(err)
	}
	if len(metrics) != 2 || metrics[0].Kind != "damage_per_round" || metrics[0].Value != 100 || metrics[0].Aggregation != "arithmetic_mean_before_normalization" {
		t.Fatalf("raw samples (0,200) must aggregate to 100, not capped 0.5: %+v", metrics)
	}
}

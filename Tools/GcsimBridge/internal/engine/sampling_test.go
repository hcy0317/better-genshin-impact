package engine

import (
	"encoding/json"
	"reflect"
	"slices"
	"testing"
)

func TestSmallOptimizationBatchCanBeCompactedWithoutChangingEvidence(t *testing.T) {
	request := Request{SchemaVersion: "1", EngineRevision: Revision, AutoRounds: true, Seeds: []int64{17, 29, 31},
		Config: `target lvl=90 resist=0.1 hp=999999999;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;active amber;for let i=0;i<3;i=i+1 {amber attack;wait(60);}`}
	full, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	encoded, _ := json.Marshal(request)
	var object map[string]any
	_ = json.Unmarshal(encoded, &object)
	object["compactSamples"] = true
	encoded, _ = json.Marshal(object)
	_ = json.Unmarshal(encoded, &request)
	compact, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if !compact.SamplesCompacted || len(compact.SampleMetrics) != len(request.Seeds) {
		t.Fatal("search batch retained unnecessary detailed samples")
	}
	if len(compact.Parameters.Settings.CollectStats) == 0 || slices.Contains(compact.Parameters.Settings.CollectStats, "status") {
		t.Fatal("compact evaluation still collects discarded per-frame status buffers")
	}
	for i, sample := range full.Samples {
		for j, character := range sample.Characters {
			if compact.Samples[i].Characters[j].ActiveTime != character.ActiveTime {
				t.Fatal("active-time frame count changed")
			}
		}
	}
	if compact.MeanDPS != full.MeanDPS || !reflect.DeepEqual(compact.ScoredDPS, full.ScoredDPS) ||
		!reflect.DeepEqual(compact.Metrics, full.Metrics) || !reflect.DeepEqual(compact.Validation, full.Validation) ||
		!reflect.DeepEqual(compact.RoundTraces, full.RoundTraces) || !reflect.DeepEqual(compact.EnergyWindows, full.EnergyWindows) ||
		!reflect.DeepEqual(compact.InitialStats, full.InitialStats) {
		t.Fatal("compaction changed scoring or validation evidence")
	}
}

func TestHundredSamplesKeepIndependentEvidenceWithinBoundedOutput(t *testing.T) {
	config := `options duration=2;target lvl=90 resist=0.1;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;active amber;while 1 {amber attack;}`
	seeds := make([]int64, 100)
	for i := range seeds {
		seeds[i] = int64(i + 10001)
	}
	result, err := Evaluate(Request{SchemaVersion: "1", EngineRevision: Revision, Config: config, Seeds: seeds})
	if err != nil {
		t.Fatal(err)
	}
	if len(result.Samples) != 100 || len(result.ScoredDPS) != 100 || !result.Validation.Complete {
		t.Fatal("independent samples were silently reduced")
	}
	encoded, err := json.Marshal(result)
	if err != nil {
		t.Fatal(err)
	}
	if len(encoded) > 2*1024*1024 {
		t.Fatalf("large sample batch retained unnecessary frame vectors: %d bytes", len(encoded))
	}
}

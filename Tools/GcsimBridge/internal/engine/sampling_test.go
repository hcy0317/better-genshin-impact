package engine

import (
	"encoding/json"
	"testing"
)

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

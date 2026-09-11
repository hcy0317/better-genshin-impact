package engine_test

import (
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"testing"
)

func TestDeclaredWeaponWaitAdaptationIsVisibleAndDoesNotChangeSimulation(t *testing.T) {
	request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: fixedConfig, Seeds: []int64{17}}
	original, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	request.Assumptions = []string{"removed_impossible_favonius_wait:amber"}
	projected, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if projected.MeanDPS != original.MeanDPS || projected.Support != "trial" {
		t.Fatal("adaptation label changed the simulation or was hidden")
	}
	if len(projected.Assumptions) != 1 || projected.Assumptions[0] != request.Assumptions[0] {
		t.Fatal("adaptation not disclosed")
	}
}

package rotation_test

import (
	"context"
	"encoding/json"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/rotation"
	"strings"
	"testing"
)

func TestNativeSearchChangesOnlyBoundedSourceParametersAndKeepsTheProgram(t *testing.T) {
	original := &nativeflow.Program{SchemaVersion: "native-flow-v1", Source: "凯亚 e,wait(1)", Root: []nativeflow.Node{{ID: "e", Kind: "skill", Character: "kaeya"}, {ID: "w", Kind: "wait", Character: "kaeya", Seconds: 1, ValueStart: 10, ValueEnd: 11}}}
	request := rotation.Request{Base: engine.Request{Config: "active kaeya;", NativeFlow: original}, SearchSeeds: []int64{1}, ValidationSeeds: []int64{2, 3}, Budget: 12}
	result, err := rotation.Optimize(context.Background(), request, func(_ context.Context, r engine.Request) (engine.Report, error) {
		if r.NativeFlow.Source != original.Source || r.NativeFlow.Root[0].Kind != "skill" {
			t.Fatal("optimizer changed the control program")
		}
		seconds := r.NativeFlow.Root[1].Seconds
		if seconds < .5 || seconds > 1.5 {
			t.Fatalf("parameter outside original bounds: %v", seconds)
		}
		dps := 100 + seconds
		return engine.Report{MeanDPS: dps, ScoredDPS: []float64{dps, dps}, Validation: engine.Validation{State: "passed", Complete: true}}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	encoded, _ := json.Marshal(result)
	var decoded map[string]any
	_ = json.Unmarshal(encoded, &decoded)
	if decoded["native"] != true {
		t.Fatalf("not a native source result: %s", encoded)
	}
	changes := decoded["nativeChanges"].([]any)
	if len(changes) != 1 || changes[0].(map[string]any)["node"] != "w" || changes[0].(map[string]any)["value"] != 1.5 {
		t.Fatalf("wrong bounded changes: %s", encoded)
	}
	if original.Root[1].Seconds != 1 {
		t.Fatal("request baseline was mutated")
	}
}

func TestRotationSearchActuallyReordersActionsAndIndependentlyValidates(t *testing.T) {
	r := rotation.Request{Base: engine.Request{Config: "__BETTERGI_ROTATION__"}, Actions: []rotation.Action{{Character: "amber", Kind: "burst"}, {Character: "amber", Kind: "skill"}}, SearchSeeds: []int64{1}, ValidationSeeds: []int64{2, 3}, Budget: 12}
	result, err := rotation.Optimize(context.Background(), r, func(_ context.Context, e engine.Request) (engine.Report, error) {
		dps := 100.0
		if strings.Index(e.Config, "amber skill") < strings.Index(e.Config, "amber burst") {
			dps = 120
		}
		return engine.Report{MeanDPS: dps, ScoredDPS: []float64{dps, dps}, Validation: engine.Validation{State: "passed", Complete: true}}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(result.Actions) != 2 || result.Actions[0].Kind != "skill" || result.Report.MeanDPS != 120 {
		t.Fatalf("no real search: %+v", result)
	}
}

func TestTimeBoundedAttackIRRunsOnRealGcsim(t *testing.T) {
	r := rotation.Request{Base: engine.Request{Config: `options duration=8; target lvl=90 resist=0.1;
amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
__BETTERGI_ROTATION__`}, Actions: []rotation.Action{{Character: "amber", Kind: "skill"}, {Character: "amber", Kind: "attack_seconds", Seconds: 2}}, SearchSeeds: []int64{17}, ValidationSeeds: []int64{29, 31}, Budget: 8}
	result, err := rotation.Optimize(context.Background(), r, func(_ context.Context, e engine.Request) (engine.Report, error) { return engine.Evaluate(e) })
	if err != nil {
		t.Fatal(err)
	}
	if result.Report == nil || result.Report.MeanDPS <= 0 || !result.Report.Validation.Complete {
		t.Fatalf("IR did not run on actual engine: %+v", result)
	}
}

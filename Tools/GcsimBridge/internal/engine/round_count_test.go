package engine

import (
	"encoding/json"
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"strings"
	"testing"
)

func TestRoundCountEndsInfiniteScriptAtRealBoundaries(t *testing.T) {
	config := `target lvl=90 resist=0.1 hp=999999999;
amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
active amber; let i=0; while 1 { amber attack; for let j=0;j<2;j=j+1 {wait(10);} wait(30+i*15); i=i+1; }`
	raw, _ := json.Marshal(map[string]any{"schemaVersion": "1", "engineRevision": Revision, "config": config, "seeds": []int64{17, 29}, "autoRounds": true, "roundCount": 3})
	var request Request
	if err := json.Unmarshal(raw, &request); err != nil {
		t.Fatal(err)
	}
	result, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if result.StopMode != "loop_count" || len(result.RoundTraces) != 2 {
		t.Fatalf("wrong termination: %+v", result)
	}
	if result.DurationSeconds >= 600 {
		t.Fatal("reported resource ceiling instead of actual elapsed time")
	}
	if !result.Validation.Complete {
		t.Fatalf("real completed rounds did not qualify: %+v", result.Validation)
	}
	for i, trace := range result.RoundTraces {
		if trace.State != "complete" || len(trace.Rounds) != 3 {
			t.Fatalf("trace=%+v", trace)
		}
		if trace.Rounds[2].DurationSeconds <= trace.Rounds[0].DurationSeconds {
			t.Fatal("fixed time substituted for actual rounds")
		}
		if result.Samples[i].Duration != trace.Rounds[2].EndFrame+1 {
			t.Fatalf("extra loop or tail: duration=%d end=%d", result.Samples[i].Duration, trace.Rounds[2].EndFrame)
		}
	}
}

const countPrefix = `target lvl=90 resist=0.1 hp=1; amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90; active amber;`

func TestRoundCountLinearAndNativeUseSamePerSampleCount(t *testing.T) {
	for _, count := range []int{1, 3, 5} {
		for _, native := range []bool{false, true} {
			t.Run(fmt.Sprintf("count%d-native%t", count, native), func(t *testing.T) {
				request := Request{SchemaVersion: "1", EngineRevision: Revision, Config: countPrefix + `amber attack; wait(61);`, Seeds: []int64{17, 29}, AutoRounds: true, RoundCount: count}
				if native {
					request.Config = countPrefix
					request.NativeFlow = &nativeflow.Program{SchemaVersion: "native-flow-v1", Source: "amber attack(0.5)", Loop: count != 5, Root: []nativeflow.Node{{ID: "attack", Kind: "attack", Character: "amber", Seconds: 0.5, Line: 1}}}
				}
				before, _ := json.Marshal(request)
				result, err := Evaluate(request)
				if err != nil {
					t.Fatal(err)
				}
				after, _ := json.Marshal(request)
				if string(before) != string(after) {
					t.Fatal("source request mutated")
				}
				if len(result.Samples) != 2 || result.MeanDPS <= 0 || result.DurationSeconds >= 600 {
					t.Fatalf("samples=%d DPS=%v duration=%v", len(result.Samples), result.MeanDPS, result.DurationSeconds)
				}
				for i, trace := range result.RoundTraces {
					if trace.State != "complete" || len(trace.Rounds) != count {
						t.Fatalf("trace=%+v", trace)
					}
					if result.Samples[i].Duration != trace.Rounds[count-1].EndFrame+1 {
						t.Fatal("extra iteration or missing final SDK tick")
					}
				}
			})
		}
	}
}

func TestRoundCountRejectsIncompleteOrUnobservableScripts(t *testing.T) {
	for _, tc := range []struct{ name, script, want string }{
		{"short", "for let i=0;i<2;i=i+1 {amber attack;wait(10);}", "round_count_incomplete"},
		{"empty", "while 1 {}", "round_count_incomplete"},
		{"ambiguous", "while 1 {amber attack;} while 1 {amber attack;}", "round_count_unavailable"},
		{"blocked", "while 1 {wait(36001);}", "trajectory_limit"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			result, err := Evaluate(Request{SchemaVersion: "1", EngineRevision: Revision, Config: countPrefix + tc.script, Seeds: []int64{17}, AutoRounds: true, RoundCount: 3})
			if err == nil || !strings.Contains(err.Error(), tc.want) || result.Validation.Complete {
				t.Fatalf("err=%v validation=%+v", err, result.Validation)
			}
		})
	}
}

func TestRoundCountNativeOnceAndRequiredFailuresRemainReal(t *testing.T) {
	opening := nativeflow.Block{Declaration: nativeflow.Node{ID: "opening-def", Kind: "segment", Line: 1}, Nodes: []nativeflow.Node{{ID: "opening-wait", Kind: "wait", Character: "amber", Seconds: 1, Line: 1}}}
	p := &nativeflow.Program{SchemaVersion: "native-flow-v1", Source: "call(opening,once=battle); amber attack(0.5)", Loop: true, Blocks: map[string]nativeflow.Block{"opening": opening}, Root: []nativeflow.Node{
		{ID: "call", Kind: "call", Args: []string{"opening"}, Options: map[string]string{"once": "battle", "required": "true"}, Line: 1},
		{ID: "attack", Kind: "attack", Character: "amber", Seconds: 0.5, Line: 1},
	}}
	request := Request{SchemaVersion: "1", EngineRevision: Revision, Config: countPrefix, Seeds: []int64{17}, AutoRounds: true, RoundCount: 3, NativeFlow: p}
	result, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	for _, event := range result.NativeFlowTraces[0].Events {
		if event.Kind == "call" && event.Detail == "opening" {
			calls++
		}
	}
	if calls != 1 {
		t.Fatalf("opening executed %d times", calls)
	}
	p.Root = []nativeflow.Node{{ID: "burst", Kind: "burst", Character: "amber", Line: 1, Options: map[string]string{"required": "true", "timeout": "0.01"}}}
	result, err = Evaluate(request)
	if err == nil || (!strings.Contains(err.Error(), "round_count_incomplete") && !strings.Contains(err.Error(), "native_flow_indeterminate")) || result.Validation.Complete {
		t.Fatalf("failed required action qualified: %v", err)
	}
}

func TestRoundCountPreservesAllThousandIndependentSamples(t *testing.T) {
	seeds := make([]int64, 1000)
	for i := range seeds {
		seeds[i] = int64(i + 1000)
	}
	result, err := Evaluate(Request{SchemaVersion: "1", EngineRevision: Revision, Config: countPrefix + `while 1 {amber attack;wait(30);}`, Seeds: seeds, AutoRounds: true, RoundCount: 3})
	if err != nil {
		t.Fatal(err)
	}
	if len(result.RoundTraces) != 1000 || len(result.ScoredDPS) != 1000 || result.SamplingIterations != 1000 {
		t.Fatal("independent samples dropped")
	}
	for _, trace := range result.RoundTraces {
		if len(trace.Rounds) != 3 || trace.State != "complete" {
			t.Fatal("sample did not contain three completed rounds")
		}
	}
}

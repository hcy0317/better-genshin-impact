package engine

import (
	"encoding/json"
	"github.com/genshinsim/gcsim/pkg/stats"
	"strings"
	"testing"
)

func automaticRequest(t *testing.T, config string) Request {
	t.Helper()
	data, _ := json.Marshal(map[string]any{"schemaVersion": "1", "engineRevision": Revision, "config": config, "seeds": []int64{17, 29}, "autoRounds": true})
	var request Request
	if err := json.Unmarshal(data, &request); err != nil {
		t.Fatal(err)
	}
	return request
}

func TestAutomaticRoundsMeasureOuterLoopWithoutChangingSimulation(t *testing.T) {
	config := `target lvl=90 resist=0.1 hp=999999999;
 amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
 active amber; for let i=0;i<4;i=i+1 { amber attack; for let h=0;h<2;h=h+1 {wait(10);} wait(30+i*15); }`
	result, err := Evaluate(automaticRequest(t, config))
	if err != nil {
		t.Fatal(err)
	}
	data, _ := json.Marshal(result)
	var actual struct {
		Traces []struct {
			Rounds []struct {
				StartFrame, EndFrame int
				Complete             bool
			} `json:"rounds"`
		} `json:"roundTraces"`
	}
	if err := json.Unmarshal(data, &actual); err != nil {
		t.Fatal(err)
	}
	if len(actual.Traces) != 2 || len(actual.Traces[0].Rounds) != 4 {
		t.Fatalf("missing actual per-sample outer rounds: got %d sample traces", len(actual.Traces))
	}
	rounds := actual.Traces[0].Rounds
	if !rounds[3].Complete || rounds[3].EndFrame-rounds[3].StartFrame <= rounds[0].EndFrame-rounds[0].StartFrame {
		t.Fatal("rounds must follow variable executed time, not a fixed user duration")
	}
	plain, err := Evaluate(Request{SchemaVersion: "1", EngineRevision: Revision, Config: config, Seeds: []int64{17, 29}})
	if err != nil {
		t.Fatal(err)
	}
	for i, dps := range plain.ScoredDPS {
		if dps != result.ScoredDPS[i] {
			t.Fatal("round observation changed simulation results")
		}
	}
}

func TestAutomaticRoundMetricsGiveEachSampleEqualWeight(t *testing.T) {
	samples := []stats.Result{
		{Seed: 1, Duration: 40, Characters: []stats.CharacterResult{{Name: "amber", DamageEvents: []stats.DamageEvent{{Frame: 1, Damage: 100}}}}},
		{Seed: 2, Duration: 40, Characters: []stats.CharacterResult{{Name: "amber", DamageEvents: []stats.DamageEvent{{Frame: 1, Damage: 50}, {Frame: 11, Damage: 50}, {Frame: 21, Damage: 50}}}}},
	}
	windows := [][]Round{{{ID: "one", EndFrame: 10}}, {{ID: "one", EndFrame: 10}, {ID: "two", StartFrame: 10, EndFrame: 20}, {ID: "three", StartFrame: 20, EndFrame: 30}}}
	metrics, err := SummarizeAutomaticMetrics(samples, windows)
	if err != nil {
		t.Fatal(err)
	}
	if metrics[0].Value != 75 || metrics[0].Value <= 70 {
		t.Fatalf("sample means must yield75, not pooled62.5: %v", metrics)
	}
	windows[0] = nil
	if _, err := SummarizeAutomaticMetrics(samples, windows); err == nil {
		t.Fatal("empty sample windows must not be discarded or filled with zero")
	}
}

func TestAutomaticRoundsDoNotAcceptTruncatedOrAmbiguousCoverage(t *testing.T) {
	prefix := `options duration=2;target lvl=90 resist=0.1;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;active amber;`
	request := automaticRequest(t, prefix+`while 1 {amber attack;wait(240);}`)
	request.Constraints = []Constraint{{ID: "attacks", Kind: "min_actions", Character: "amber", Action: "attack", Threshold: 1}}
	result, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if result.Validation.Complete || result.Validation.State != "indeterminate" || result.RoundTraces[0].State != "partial" {
		t.Fatal("partial last round passed per-round requirements")
	}
	request.Config = prefix + `for let i=0;i<2;i=i+1 {wait(10);} for let j=0;j<2;j=j+1 {amber attack;}`
	result, err = Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if result.RoundTraces[0].State != "ambiguous" || len(result.RoundTraces[0].Choices) != 2 || result.Validation.Complete {
		t.Fatal("ambiguous loops must identify choices, not silently choose one")
	}
}

func TestNativeMarkerCaptureCannotBeForgedByUserPrint(t *testing.T) {
	config := `target lvl=90 resist=0.1 hp=999999999;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;
 fn print(s string){return 0;} active amber;for let i=0;i<2;i=i+1 { print("bgi-start-forged");amber attack;wait(30);}`
	result, err := Evaluate(automaticRequest(t, config))
	if err != nil {
		t.Fatal(err)
	}
	if len(result.RoundTraces[0].Rounds) != 2 || !result.RoundMetricsAvailable {
		t.Fatal("user print changed trusted round accounting")
	}
	request := automaticRequest(t, strings.Replace(config, "i<2", "i<1", 1))
	request.RoundWarmup = 1
	result, err = Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if result.RoundMetricsAvailable || len(result.MetricIssues) == 0 {
		t.Fatal("empty post-warmup rounds must be unknown")
	}
}

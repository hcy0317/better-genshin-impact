package engine

import (
	"math"
	"strings"
	"testing"
)

func TestDamageModeStopsOnFiniteScriptInsteadOfDuration(t *testing.T) {
	config := `options duration=1; target lvl=90 resist=0.1 hp=999999999;
 amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
 active amber; for let i=0; i<4; i=i+1 { amber attack; wait(60); }`
	request := Request{SchemaVersion: "1", EngineRevision: Revision, Config: config, Seeds: []int64{17, 29}}
	result, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if result.Samples[0].Duration <= 240 || result.Validation.State != "passed" {
		t.Fatalf("unexpected finite-script result: %+v", result.Validation)
	}
	request.Config = strings.Replace(config, " hp=999999999", "", 1)
	fixed, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if fixed.Samples[0].Duration > 61 {
		t.Fatalf("fixed duration changed: %d", fixed.Samples[0].Duration)
	}
}

func TestPermanentEnemyBuffsOutliveOldDurationInDamageMode(t *testing.T) {
	config := `target lvl=90 resist=0.1 hp=999999999;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;active amber;amber attack;wait(180);amber attack;wait(60);`
	request := Request{SchemaVersion: "1", EngineRevision: Revision, Config: config, Seeds: []int64{17}}
	base, err := Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	for _, kind := range []string{"resistance", "defense_reduction"} {
		for _, anchor := range []string{"start", "action"} {
			buff := Buff{ID: "permanent", Kind: kind, Target: "all_enemies", Value: 0.2, Unit: "fraction", Anchor: anchor, DurationFrames: -1, Relationship: "additional"}
			if kind == "resistance" {
				buff.Element = "physical"
				buff.Value = -0.1
			}
			if anchor == "action" {
				buff.SourceCharacter = "amber"
				buff.Action = "attack"
			}
			request.Buffs = []Buff{buff}
			actual, err := Evaluate(request)
			if err != nil {
				t.Fatal(err)
			}
			if math.Abs(actual.MeanDPS/base.MeanDPS-10.0/9) > 1e-10 {
				t.Fatalf("%s/%s permanent effect expired: ratio%v", kind, anchor, actual.MeanDPS/base.MeanDPS)
			}
		}
	}
}

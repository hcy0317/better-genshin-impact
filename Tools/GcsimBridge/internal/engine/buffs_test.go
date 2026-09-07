package engine_test

import (
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"math"
	"strings"
	"testing"
)

func TestPermanentStatBuffMatchesNativeAdditionalStats(t *testing.T) {
	request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: fixedConfig, Seeds: []int64{17}}
	request.Config += "\namber add stats atk=100;\n"
	reference, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	request.Config = fixedConfig
	request.Buffs = []engine.Buff{{ID: "manual-food", Kind: "stat", Target: "amber", Stat: "atk", Value: 100, Unit: "flat", DurationFrames: -1, Anchor: "start", Relationship: "additional"}}
	actual, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if actual.MeanDPS != reference.MeanDPS {
		t.Fatalf("manual stat mapping: got %v want %v", actual.MeanDPS, reference.MeanDPS)
	}
	if len(actual.Assumptions) == 0 || len(actual.BuffActivations) != 1 || actual.BuffActivations[0][0].Frame != 0 {
		t.Fatal("manual assumption/activation identity missing")
	}
}

func TestActionAnchoredBuffFollowsSuccessfulDelayedCast(t *testing.T) {
	var frames []int
	for _, delay := range []int{0, 120} {
		config := strings.ReplaceAll(fixedConfig, "while 1 { amber attack:3; }", fmt.Sprintf("delay(%d); amber skill; amber attack:3;", delay))
		request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: config, Seeds: []int64{17},
			Buffs: []engine.Buff{{ID: "cast-buff", Kind: "stat", Target: "amber", Stat: "atk", Unit: "flat", Value: 100, DurationFrames: 60, Anchor: "action", SourceCharacter: "amber", Action: "skill", Relationship: "pending_review"}},
		}
		result, err := engine.Evaluate(request)
		if err != nil {
			t.Fatal(err)
		}
		if len(result.BuffActivations) != 1 || len(result.BuffActivations[0]) != 1 {
			t.Fatalf("wrong activation count: %+v", result.BuffActivations)
		}
		activation := result.BuffActivations[0][0]
		cast := -1
		for _, action := range result.Samples[0].Characters[0].ActionEvents {
			if action.Action == "skill" {
				cast = action.Frame
				break
			}
		}
		if cast < 0 || activation.Frame != cast || activation.ExpiresAt != cast+60 {
			t.Fatalf("buff not bound to successful action: %+v; cast %d", activation, cast)
		}
		frames = append(frames, cast)
	}
	if frames[1]-frames[0] != 120 {
		t.Fatalf("delay not preserved: %v", frames)
	}
}

func TestRoundBuffUsesDeclaredSimulationBoundaries(t *testing.T) {
	request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: fixedConfig, Seeds: []int64{17},
		Rounds: []engine.Round{{ID: "first", StartFrame: 0, EndFrame: 600}, {ID: "second", StartFrame: 600, EndFrame: 1200}},
		Buffs:  []engine.Buff{{ID: "cycle", Kind: "stat", Target: "amber", Stat: "atk", Value: 100, Unit: "flat", DurationFrames: 60, Anchor: "round", Relationship: "additional"}},
	}
	result, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if len(result.BuffActivations[0]) != 2 || result.BuffActivations[0][0].Frame != 0 || result.BuffActivations[0][1].Frame != 600 {
		t.Fatalf("round boundaries not preserved: %+v", result.BuffActivations)
	}
}

func TestNativeDebuffsAndAttackTagRestriction(t *testing.T) {
	request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: fixedConfig, Seeds: []int64{17}}
	base, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		buff  engine.Buff
		ratio float64
	}{
		{engine.Buff{Kind: "resistance", Target: "all_enemies", Element: "physical", Value: -0.1}, 1 / 0.9},
		{engine.Buff{Kind: "defense_reduction", Target: "all_enemies", Value: 0.2}, 10.0 / 9},
		{engine.Buff{Kind: "attack_bonus", Target: "amber", AttackTag: "normal", Value: 0.4}, 1.4},
		{engine.Buff{Kind: "attack_bonus", Target: "amber", AttackTag: "burst", Value: 0.4}, 1},
	} {
		t.Run(test.buff.Kind+test.buff.AttackTag, func(t *testing.T) {
			buff := test.buff
			buff.ID = "effect"
			buff.Unit = "fraction"
			buff.Anchor = "start"
			buff.DurationFrames = -1
			buff.Relationship = "additional"
			request.Buffs = []engine.Buff{buff}
			result, err := engine.Evaluate(request)
			if err != nil {
				t.Fatal(err)
			}
			if math.Abs(result.MeanDPS/base.MeanDPS-test.ratio) > 1e-10 {
				t.Fatalf("wrong calculation position: ratio=%v want=%v", result.MeanDPS/base.MeanDPS, test.ratio)
			}
		})
	}
}

func TestCoverageApproximationAndUnsupportedReplacementAreExplicit(t *testing.T) {
	request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: fixedConfig + "\namber add stats atk=50;", Seeds: []int64{17}}
	reference, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	request.Config = fixedConfig
	request.Buffs = []engine.Buff{{ID: "approx", Kind: "stat", Target: "amber", Stat: "atk", Value: 100, Unit: "flat", DurationFrames: -1, Anchor: "start", CoverageRatio: number(0.5), Source: "hypothetical 50% time coverage"}}
	actual, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if actual.MeanDPS != reference.MeanDPS || actual.Support != "trial" || actual.ManualBuffs[0].Relationship != "pending_review" || len(actual.Assumptions) != 2 {
		t.Fatalf("approximation not preserved: %+v", actual.Assumptions)
	}
	request.Buffs[0].Relationship = "replace"
	if _, err := engine.Evaluate(request); err == nil {
		t.Fatal("native replacement was silently double-applied")
	}
}

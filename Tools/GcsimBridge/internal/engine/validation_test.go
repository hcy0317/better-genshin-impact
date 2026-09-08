package engine_test

import (
	"github.com/genshinsim/gcsim/pkg/stats"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"testing"
)

func TestEveryTrajectoryMustCompleteRequiredBurst(t *testing.T) {
	seeds := []int64{11, 12}
	rounds := []engine.Round{{ID: "scored", StartFrame: 0, EndFrame: 100}}
	constraints := []engine.Constraint{{ID: "q", Kind: "min_actions", Character: "amber", Action: "burst", Threshold: 1}}
	samples := []stats.Result{
		{Seed: 11, Duration: 100, Characters: []stats.CharacterResult{{Name: "amber", ActionEvents: []stats.ActionEvent{{Frame: 10, Action: "burst"}, {Frame: 50, Action: "burst"}}}}},
		{Seed: 12, Duration: 100, Characters: []stats.CharacterResult{{Name: "amber"}}},
	}
	verdict := engine.ValidateBatch(seeds, rounds, constraints, samples)
	if verdict.State != "failed" {
		t.Fatalf("Q=(2,0) must not pass by averaging: %+v", verdict)
	}
	if len(verdict.Checks) != 2 || verdict.Checks[0].State != "passed" || verdict.Checks[1].State != "failed" {
		t.Fatalf("missing per-trajectory evidence: %+v", verdict)
	}
}

func TestIncompleteAndUnknownValidationCannotPass(t *testing.T) {
	rounds := []engine.Round{{ID: "later", StartFrame: 100, EndFrame: 200}}
	constraints := []engine.Constraint{{ID: "q", Kind: "min_actions", Character: "amber", Action: "burst", Threshold: 1}}
	passed := stats.Result{Seed: 1, Duration: 200, Characters: []stats.CharacterResult{{Name: "amber", ActionEvents: []stats.ActionEvent{{Frame: 120, Action: "burst"}}}}}
	for name, samples := range map[string][]stats.Result{"empty": nil, "missing": {passed}, "truncated": {passed, {Seed: 2, Duration: 150, Characters: passed.Characters}}} {
		t.Run(name, func(t *testing.T) {
			v := engine.ValidateBatch([]int64{1, 2}, rounds, constraints, samples)
			if v.State != "indeterminate" || v.Complete {
				t.Fatalf("invalid coverage passed: %+v", v)
			}
		})
	}
	unknown := engine.ValidateBatch([]int64{1}, rounds, []engine.Constraint{{ID: "shield", Kind: "min_shield", Character: "amber", Threshold: 1}}, []stats.Result{passed})
	if unknown.State != "indeterminate" || unknown.Checks[0].Observed != nil {
		t.Fatalf("unknown shield was treated as zero/known: %+v", unknown)
	}
	failed := passed
	failed.Characters = []stats.CharacterResult{{Name: "amber"}}
	mixed := engine.ValidateBatch([]int64{1, 2}, rounds, constraints, []stats.Result{failed})
	if mixed.State != "failed" || mixed.Complete {
		t.Fatalf("known violation hidden by missing data: %+v", mixed)
	}
}

func TestSustainedBurstChecksLaterScoredRounds(t *testing.T) {
	request := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17},
		Config: `options duration=60 workers=1 iteration=1; target lvl=90 resist=0.1;
amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
active amber; while 1 { amber burst; }`,
		Rounds:      []engine.Round{{ID: "opening", StartFrame: 0, EndFrame: 1200}, {ID: "later-1", StartFrame: 1200, EndFrame: 2400}, {ID: "later-2", StartFrame: 2400, EndFrame: 3600}},
		Constraints: []engine.Constraint{{ID: "q", Kind: "min_actions", Character: "amber", Action: "burst", Threshold: 1}},
	}
	result, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if result.Validation.State != "failed" || !result.Validation.Complete {
		t.Fatalf("later energy starvation not observed: %+v", result.Validation)
	}
	if result.Validation.Checks[0].State != "passed" || result.Validation.Checks[1].State != "failed" || result.Validation.Checks[2].State != "failed" {
		t.Fatalf("opening/later rounds not distinguished: %+v", result.Validation.Checks)
	}
	if len(result.EnergyWindows) != 1 || len(result.EnergyWindows[0]) != 6 || result.EnergyWindows[0][0].Energy["amber"] <= 0 {
		t.Fatalf("missing per-round energy evidence: %+v", result.EnergyWindows)
	}
}

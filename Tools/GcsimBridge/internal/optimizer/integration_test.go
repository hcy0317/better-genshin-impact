package optimizer_test

import (
	"context"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
	"testing"
)

func TestRealGcsimTwoCharactersShareGearAcrossMultipleBuilds(t *testing.T) {
	r := fixtureRequest()
	r.EvaluationBudget = 160
	r.Characters = []optimizer.Character{
		{CharacterGoal: optimizer.CharacterGoal{Character: "amber", Weight: 1}, MinimumStats: map[string]float64{"enerRech_": 100}},
		{CharacterGoal: optimizer.CharacterGoal{Character: "kaeya", Weight: 1}},
	}
	for i := range r.Items {
		item := &r.Items[i]
		item.SetKey = "EmblemOfSeveredFate"
		key, value := "atk_", 46.6
		switch item.SlotKey {
		case "flower":
			key, value = "hp", 4780
		case "plume":
			key, value = "atk", 311
		case "circlet":
			key, value = "critRate_", 31.1
		}
		item.MainStatKey, item.MainStatValue = key, &value
		item.Substats = []engine.Substat{{Key: "critDMG_", Value: float64(7 * (i%2 + 1))}}
	}
	config := `options duration=6; target lvl=90 resist=0.1;
amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
kaeya char lvl=90/90 cons=0 talent=6,6,6; kaeya add weapon="dullblade" refine=1 lvl=90/90;
active amber; while 1 { amber attack:3; kaeya attack:3; }`
	r.Scenarios = []optimizer.Scenario{{ID: "normal", Weight: 1, Participants: []string{"amber", "kaeya"}, Evaluation: engine.Request{Config: config}},
		{ID: "skill", Weight: 1, Participants: []string{"amber", "kaeya"}, Evaluation: engine.Request{Config: config + "\n"}}}
	result, err := optimizer.Optimize(context.Background(), r, func(_ context.Context, e engine.Request) (engine.Report, error) { return engine.Evaluate(e) })
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil || len(result.Plan.Equipment) != 2 || len(result.Plan.Reports) != 2 || result.Plan.Rank.Balanced <= 0 {
		t.Fatalf("real multi-Build evaluation failed: %+v", result)
	}
	for _, report := range result.Plan.Reports {
		if report.Validation.State != "passed" || len(report.ScoredDPS) != 1 {
			t.Fatalf("incomplete independent trajectory: %+v", report.Validation)
		}
	}
}

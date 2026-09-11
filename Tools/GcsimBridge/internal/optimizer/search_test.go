package optimizer_test

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
)

func TestUnavailableCandidatesKeepBoundedActionableExamples(t *testing.T) {
	r := fixtureRequest()
	r.EvaluationBudget = 16
	r.Exact = false
	result, err := optimizer.Optimize(context.Background(), r, func(context.Context, engine.Request) (engine.Report, error) {
		return engine.Report{}, errors.New("trajectory_limit: 检查循环等待条件")
	})
	if err != nil || result.Plan != nil || result.Status != "indeterminate" {
		t.Fatalf("invalid result: %+v / %v", result, err)
	}
	data, _ := json.Marshal(result)
	var fields map[string]json.RawMessage
	_ = json.Unmarshal(data, &fields)
	if !strings.Contains(string(fields["issues"]), "循环等待条件") {
		t.Fatal("specific candidate failure was lost")
	}
}

func TestUnchangedEquipmentValidatesEachIndependentBatchOnlyOnce(t *testing.T) {
	r := fixtureRequest()
	r.Characters[0].Current = []int{1, 3, 5, 7, 9}
	r.Characters[1].Current = []int{0, 2, 4, 6, 8}
	r.ValidationSeeds = []int64{401, 503, 601}
	validationCalls := 0
	result, err := optimizer.Optimize(context.Background(), r, func(ctx context.Context, evaluation engine.Request) (engine.Report, error) {
		if evaluation.Seeds[0] == 401 {
			validationCalls++
			if len(evaluation.Seeds) != 3 {
				t.Fatal("validation seeds were reduced")
			}
		}
		return testEvaluator(ctx, evaluation)
	})
	if err != nil || result.Status != "feasible_baseline" {
		t.Fatalf("%+v / %v", result, err)
	}
	if validationCalls != 1 {
		t.Fatalf("identical final equipment was validated %d times", validationCalls)
	}
}

func fixtureRequest() optimizer.Request {
	r := optimizer.Request{SchemaVersion: "1", Mode: "balanced", Inventory: engine.InventoryRef{UID: "fixture", ScanSessionID: "scan", CatalogVersion: "catalog", SnapshotDigest: "snapshot"}, SearchSeeds: []int64{1}, ValidationSeeds: []int64{2}, EvaluationBudget: 1000, Exact: true}
	for i, slot := range []string{"flower", "plume", "sands", "goblet", "circlet"} {
		for j := range 2 {
			value := float64(j + 1)
			r.Items = append(r.Items, optimizer.Item{Artifact: engine.Artifact{ScanIndex: i*2 + j, SlotKey: slot, SetKey: "test", MainStatKey: "atk", MainStatValue: &value}})
		}
	}
	r.Characters = []optimizer.Character{{CharacterGoal: optimizer.CharacterGoal{Character: "a", Weight: 1}}, {CharacterGoal: optimizer.CharacterGoal{Character: "b", Weight: 1}}}
	r.Scenarios = []optimizer.Scenario{{ID: "team", Weight: 1, Evaluation: engine.Request{Config: "shared-team"}}}
	return r
}

func testEvaluator(_ context.Context, r engine.Request) (engine.Report, error) {
	dps := 0.0
	for character, items := range r.Equipment {
		weight := 1.0
		if character == "a" {
			weight = 2
		}
		for _, item := range items {
			dps += *item.MainStatValue * weight
		}
	}
	return engine.Report{MeanDPS: dps, Validation: engine.Validation{State: "passed", Complete: true}}, nil
}

func TestJointAllocationUsesOneSharedInventory(t *testing.T) {
	r := fixtureRequest()
	result, err := optimizer.Optimize(context.Background(), r, testEvaluator)
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil || !result.Plan.Qualified || len(result.Plan.Equipment) != 2 {
		t.Fatalf("expected two complete outfits: %+v", result)
	}
	used := map[int]bool{}
	for _, items := range result.Plan.Equipment {
		if len(items) != 5 {
			t.Fatal("missing slots")
		}
		for _, id := range items {
			if used[id] {
				t.Fatal("one physical artifact assigned twice")
			}
			used[id] = true
		}
	}
	if result.Plan.Rank.Balanced != 25 {
		t.Fatalf("joint oracle should allocate every stronger piece to a: %+v", result.Plan.Rank)
	}
}

func TestMinimumStatsCannotBeSilentlyIgnored(t *testing.T) {
	r := fixtureRequest()
	r.Characters[0].MinimumStats = map[string]float64{"enerRech_": 180}
	result, err := optimizer.Optimize(context.Background(), r, testEvaluator)
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan != nil || result.Status != "indeterminate" {
		t.Fatalf("missing required stat evidence must prevent recommendation: %+v", result)
	}
}

func TestHeuristicExchangesConflictingPiecesAcrossCharacters(t *testing.T) {
	r := fixtureRequest()
	r.Exact = false
	r.EvaluationBudget = 24
	r.Characters[0].Current = []int{0, 2, 4, 6, 8}
	r.Characters[1].Current = []int{1, 3, 5, 7, 9}
	result, err := optimizer.Optimize(context.Background(), r, testEvaluator)
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil || result.Plan.Rank.Balanced != 25 || result.Evaluations > 24 {
		t.Fatalf("must exchange conflicts within bounded budget: %+v", result)
	}
}

func TestAutomaticReferencesAreFrozenBeforePeakRanking(t *testing.T) {
	r := fixtureRequest()
	r.Mode = "peak"
	for i := range r.Characters {
		r.Characters[i].Targets = []optimizer.Target{{Scenario: "team", Metric: "damage_per_round", Weight: 1}}
	}
	r.Characters[0].Weight = 4
	result, err := optimizer.Optimize(context.Background(), r, func(ctx context.Context, e engine.Request) (engine.Report, error) {
		report, _ := testEvaluator(ctx, e)
		for owner, pieces := range e.Equipment {
			value := 0.0
			for _, piece := range pieces {
				value += *piece.MainStatValue
			}
			report.Metrics = append(report.Metrics, engine.Metric{Character: owner, Kind: "damage_per_round", Value: value})
		}
		return report, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil || result.Plan.Rank.Peak != .9 {
		t.Fatalf("expected fixed references 10/10 and weighted retention .9: %+v", result)
	}
}

func TestDuplicateBuildTargetsDoNotMultiplyRoleWeight(t *testing.T) {
	r := fixtureRequest()
	r.Mode = "peak"
	for i := range r.Characters {
		r.Characters[i].Targets = []optimizer.Target{{Scenario: "team", Metric: "damage_per_round", Weight: 1, Reference: 10}}
	}
	r.Characters[0].Targets = append(r.Characters[0].Targets, optimizer.Target{Scenario: "team", Metric: "damage_per_round", Weight: 2, Reference: 10})
	if _, err := optimizer.Optimize(context.Background(), r, testEvaluator); err == nil {
		t.Fatal("conflicting duplicate target must be rejected")
	}
}

func TestFailedBaselineCannotBlockLowerDpsQualifiedPlan(t *testing.T) {
	r := fixtureRequest()
	r.Characters = r.Characters[:1]
	r.Characters[0].Current = []int{1, 3, 5, 7, 9}
	result, err := optimizer.Optimize(context.Background(), r, func(ctx context.Context, e engine.Request) (engine.Report, error) {
		report, _ := testEvaluator(ctx, e)
		if report.MeanDPS == 20 {
			report.Validation.State = "failed"
		}
		return report, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil || !result.Plan.Qualified || result.Plan.Rank.Balanced != 18 {
		t.Fatalf("qualified lower DPS must be eligible: %+v", result)
	}
}

func TestFinalBatchViolationNeverBecomesRecommendation(t *testing.T) {
	r := fixtureRequest()
	result, err := optimizer.Optimize(context.Background(), r, func(ctx context.Context, e engine.Request) (engine.Report, error) {
		report, _ := testEvaluator(ctx, e)
		if e.Seeds[0] == 2 {
			report.Validation.State = "failed"
		}
		return report, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan != nil || result.Status != "validation_failed" {
		t.Fatalf("must reject final violation: %+v", result)
	}
}

func TestNoisyImprovementIsNotReportedAsConfirmed(t *testing.T) {
	r := fixtureRequest()
	r.Characters = r.Characters[:1]
	r.Characters[0].Current = []int{0, 2, 4, 6, 8}
	r.ValidationSeeds = []int64{2, 3, 4}
	result, err := optimizer.Optimize(context.Background(), r, func(ctx context.Context, e engine.Request) (engine.Report, error) {
		report, _ := testEvaluator(ctx, e)
		if len(e.Seeds) == 3 {
			if report.MeanDPS == 20 {
				report.ScoredDPS = []float64{0, 50, 10}
			} else {
				report.ScoredDPS = []float64{10, 10, 10}
			}
		}
		return report, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.Status != "feasible_uncertain" {
		t.Fatalf("noisy gain must be explicit: %+v", result)
	}
}

func TestGlobalProtectionAlsoFreezesASelectedCharacter(t *testing.T) {
	r := fixtureRequest()
	r.ProtectedCharacters = []string{"a"}
	r.Characters[0].Current = []int{0, 2, 4, 6, 8}
	for i := range r.Items {
		if i%2 == 0 {
			r.Items[i].Location = "a"
		}
	}
	for i := 0; i < 5; i++ {
		copy := r.Items[i*2+1]
		copy.ScanIndex = 10 + i
		value := 3.0
		copy.MainStatValue = &value
		r.Items = append(r.Items, copy)
	}
	result, err := optimizer.Optimize(context.Background(), r, testEvaluator)
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil {
		t.Fatalf("missing protected plan: %+v", result)
	}
	for _, id := range result.Plan.Equipment["a"] {
		if id%2 != 0 {
			t.Fatal("protection allowed replacing an equipped artifact")
		}
	}
}

func TestHeuristicConstructsRequiredSetRatherThanRelyingOnRandomChance(t *testing.T) {
	r := fixtureRequest()
	r.Exact = false
	r.EvaluationBudget = 16
	r.Characters = r.Characters[:1]
	r.Characters[0].RequiredSets = map[string]int{"required": 4}
	r.Items = nil
	for slotIndex, slot := range []string{"flower", "plume", "sands", "goblet", "circlet"} {
		for j := 0; j < 20; j++ {
			value := 1.0
			set := "other"
			if j == 10 && slotIndex < 4 {
				set = "required"
			}
			r.Items = append(r.Items, optimizer.Item{Artifact: engine.Artifact{ScanIndex: slotIndex*30 + j, SlotKey: slot, SetKey: set, MainStatKey: "atk", MainStatValue: &value}})
		}
	}
	result, err := optimizer.Optimize(context.Background(), r, testEvaluator)
	if err != nil {
		t.Fatal(err)
	}
	if result.Plan == nil {
		t.Fatalf("four-piece feasibility must guide candidate construction: %+v", result)
	}
}

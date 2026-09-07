package optimizer_test

import (
	"context"
	"testing"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
)

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

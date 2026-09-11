package rotation_test

import (
	"context"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/rotation"
	"testing"
)

func TestUnchangedNativeBaselineDoesNotRunTheSameValidationBatchTwice(t *testing.T) {
	p := &nativeflow.Program{SchemaVersion: "native-flow-v1", Source: "fixture", Root: []nativeflow.Node{{ID: "q", Kind: "burst", Character: "jean", Options: map[string]string{"required": "true"}}}}
	validations := 0
	_, err := rotation.Optimize(context.Background(), rotation.Request{Base: engine.Request{NativeFlow: p}, SearchSeeds: []int64{1}, ValidationSeeds: []int64{2, 3}, Budget: 8}, func(ctx context.Context, r engine.Request) (engine.Report, error) {
		if len(r.Seeds) == 2 {
			validations++
		}
		return engine.Report{MeanDPS: 10, Validation: engine.Validation{State: "passed", Complete: true}, NativeQuality: &engine.NativeQuality{Source: "gcsim.sdk.shields/actions/hp", Complete: true, Samples: len(r.Seeds)}}, nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if validations != 1 {
		t.Fatalf("same independent batch evaluated %d times", validations)
	}
}

func TestNativeOptimizationActuallyRemovesOptionalOutputOnlyWithoutSafetyRegression(t *testing.T) {
	for _, unsafe := range []bool{false, true} {
		t.Run(map[bool]string{false: "safe", true: "unsafe"}[unsafe], func(t *testing.T) {
			p := &nativeflow.Program{SchemaVersion: "native-flow-v1", Source: "fixture", Root: []nativeflow.Node{{ID: "shield", Kind: "skill", Character: "zhongli", Options: map[string]string{"record": "shield", "required": "true"}}, {ID: "q", Kind: "burst", Character: "jean"}, {ID: "hit", Kind: "attack", Character: "jean", Seconds: 1, Options: map[string]string{"keep": "shield", "required": "true"}}}}
			result, err := rotation.Optimize(context.Background(), rotation.Request{Base: engine.Request{NativeFlow: p}, SearchSeeds: []int64{1}, ValidationSeeds: []int64{2, 3}, Budget: 12}, func(ctx context.Context, r engine.Request) (engine.Report, error) {
				hasQ := false
				for _, n := range r.NativeFlow.Root {
					hasQ = hasQ || n.ID == "q"
				}
				dps := 10.0
				if !hasQ {
					dps = 20
				}
				quality := &engine.NativeQuality{Source: "gcsim.sdk.shields/actions/hp", Complete: true, Samples: len(r.Seeds), MinShieldCoverage: 1, MinCriticalShieldCoverage: 1, MinPartyHP: 1}
				if unsafe && !hasQ && r.NativeFlow.DropFirstBurst {
					quality.MinCriticalShieldCoverage = .5
				}
				return engine.Report{MeanDPS: dps, Validation: engine.Validation{State: "passed", Complete: true}, NativeQuality: quality}, nil
			})
			if err != nil {
				t.Fatal(err)
			}
			if result.Report == nil {
				t.Fatal(result.Issues)
			}
			if len(result.NativeProbes) == 0 {
				t.Fatal("missing independent perturbation checks")
			}
			if unsafe {
				if result.Report.MeanDPS != 10 {
					t.Fatal("unsafe DPS gain accepted")
				}
			} else if result.Report.MeanDPS != 20 || len(result.NativeEdits) == 0 {
				t.Fatal("structural optimization was not performed")
			}
		})
	}
}

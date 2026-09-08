package rotation

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
	"math"
)

func optimizeNative(ctx context.Context, r Request, evaluate optimizer.Evaluator) (Result, error) {
	result := Result{Native: true, Issues: []string{}, NativeChanges: []nativeflow.Parameter{}}
	if evaluate == nil || r.Budget < 4 || r.Budget > 512 {
		return result, errors.New("invalid native-flow search budget")
	}
	if err := r.Base.NativeFlow.Validate(); err != nil {
		return result, err
	}
	seenSeeds := map[int64]bool{}
	for _, batch := range [][]int64{r.SearchSeeds, r.ValidationSeeds} {
		if len(batch) == 0 || len(batch) > engine.MaxEvaluationSamples {
			return result, errors.New("declared native-flow seed batches required")
		}
		for _, seed := range batch {
			if seenSeeds[seed] {
				return result, errors.New("duplicate/overlapping native-flow seeds")
			}
			seenSeeds[seed] = true
		}
	}
	original := r.Base.NativeFlow.Parameters()
	bestValues := append([]nativeflow.Parameter(nil), original...)
	run := func(values []nativeflow.Parameter, seeds []int64) (*engine.Report, error) {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		request := r.Base
		request.SchemaVersion = "1"
		request.EngineRevision = engine.Revision
		request.CompactSamples = true
		request.Seeds = seeds
		request.NativeFlow = r.Base.NativeFlow.WithParameters(values)
		report, err := evaluate(ctx, request)
		result.Evaluations++
		if err != nil {
			return nil, err
		}
		if !report.Validation.Complete || report.Validation.State != "passed" {
			return &report, errors.New("原生流程样本未通过所声明的逐轮约束")
		}
		return &report, nil
	}
	issue := func(err error) {
		if err != nil && len(result.Issues) < 8 {
			result.Issues = append(result.Issues, err.Error())
		}
	}
	baseline, baseErr := run(original, r.SearchSeeds)
	var best *engine.Report
	if baseErr == nil {
		best = baseline
	} else {
		issue(baseErr)
	}
	signature := func(values []nativeflow.Parameter) string { data, _ := json.Marshal(values); return string(data) }
	seen := map[string]bool{signature(original): true}
	for pass := 0; pass < r.Budget; pass++ {
		before := result.Evaluations
		for i := range bestValues {
			for _, delta := range []float64{-.25, .25} {
				if result.Evaluations >= r.Budget-2 || ctx.Err() != nil {
					break
				}
				candidate := append([]nativeflow.Parameter(nil), bestValues...)
				value := math.Round((candidate[i].Value+delta)*1e6) / 1e6
				if value < candidate[i].Min || value > candidate[i].Max {
					continue
				}
				candidate[i].Value = value
				key := signature(candidate)
				if seen[key] {
					continue
				}
				seen[key] = true
				report, err := run(candidate, r.SearchSeeds)
				if err != nil {
					issue(err)
					continue
				}
				if best == nil || report.MeanDPS > best.MeanDPS {
					best = report
					bestValues = candidate
				}
			}
		}
		if before == result.Evaluations || result.Evaluations >= r.Budget-2 || ctx.Err() != nil {
			break
		}
	}
	if ctx.Err() != nil {
		result.Status = "cancelled"
		return result, nil
	}
	if best == nil {
		result.Status = "budget_no_feasible"
		return result, nil
	}
	validated, err := run(bestValues, r.ValidationSeeds)
	var reference *engine.Report
	if baseErr == nil {
		reference, _ = run(original, r.ValidationSeeds)
	}
	if err != nil {
		issue(err)
		if reference == nil {
			result.Status = "validation_failed"
			return result, nil
		}
		validated = reference
		bestValues = original
	}
	result.Baseline = reference
	result.Report = validated
	if reference != nil && validated.MeanDPS <= reference.MeanDPS {
		result.Report = reference
		bestValues = original
		result.Status = "feasible_baseline"
	} else {
		result.Status = "feasible_recommendation"
		if reference != nil {
			result.Improvement = optimizer.CompareImprovement("balanced", map[string]float64{"rotation": 1}, &optimizer.Plan{Reports: map[string]engine.Report{"rotation": *validated}}, &optimizer.Plan{Reports: map[string]engine.Report{"rotation": *reference}}, len(r.ValidationSeeds))
			if result.Improvement.State != "confirmed" {
				result.Status = "feasible_uncertain"
			}
		}
	}
	for _, v := range bestValues {
		if v.Value != v.Original {
			result.NativeChanges = append(result.NativeChanges, v)
		}
	}
	if len(original) == 0 {
		result.Issues = append(result.Issues, "此流程没有可安全调整的非原子等待/普攻参数，仅验证原基线；未重排控制图或宏")
	}
	return result, nil
}

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
	bestProgram := r.Base.NativeFlow
	var bestEdits []nativeflow.StructuralEdit
	run := func(program *nativeflow.Program, values []nativeflow.Parameter, seeds []int64) (*engine.Report, error) {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		request := r.Base
		request.SchemaVersion = "1"
		request.EngineRevision = engine.Revision
		request.CompactSamples = true
		request.Seeds = seeds
		request.NativeFlow = program.WithParameters(values)
		report, err := evaluate(ctx, request)
		result.Evaluations++
		if err != nil {
			return nil, err
		}
		if !report.Validation.Complete || report.Validation.State != "passed" {
			return &report, errors.New("原生流程样本未通过所声明的逐轮约束")
		}
		if report.NativeQuality == nil || !report.NativeQuality.Complete || report.NativeQuality.Samples != len(seeds) {
			return &report, errors.New("未取得完整的原生流程护盾/进展/生命值证据，不能推荐结构候选")
		}
		return &report, nil
	}
	issue := func(err error) {
		if err != nil && len(result.Issues) < 8 {
			result.Issues = append(result.Issues, err.Error())
		}
	}
	baseline, baseErr := run(r.Base.NativeFlow, original, r.SearchSeeds)
	var best *engine.Report
	tradeoff := func(report *engine.Report) {
		if best != nil && report.MeanDPS > best.MeanDPS && !nativeSafetyNotWorse(report, best) && len(result.NativeTradeoffs) < 8 {
			result.NativeTradeoffs = append(result.NativeTradeoffs, NativeTradeoff{report.MeanDPS, best.MeanDPS, report.NativeQuality, "DPS较高但护盾、失败轮次、伤害进展或生命值至少一项退化，未推荐"})
		}
	}
	if baseErr == nil {
		best = baseline
	} else {
		issue(baseErr)
	}
	signature := func(program *nativeflow.Program, values []nativeflow.Parameter) string {
		data, _ := json.Marshal(program.WithParameters(values))
		return string(data)
	}
	seen := map[string]bool{signature(r.Base.NativeFlow, original): true}
	searchLimit := r.Budget - 6
	if r.Budget < 8 {
		searchLimit = 1
		result.Issues = append(result.Issues, "预算不足以同时搜索和独立扰动复评，本次仅核验原基线")
	}
	for pass := 0; pass < r.Budget; pass++ {
		before := result.Evaluations
		remaining := max(0, searchLimit-result.Evaluations)
		structureBudget := remaining
		if len(bestValues) > 0 {
			structureBudget = max(1, remaining/2)
		}
		parentEdits := append([]nativeflow.StructuralEdit(nil), bestEdits...)
		for _, candidate := range bestProgram.StructuralCandidates(structureBudget) {
			if result.Evaluations >= searchLimit || ctx.Err() != nil {
				break
			}
			key := signature(candidate.Program, bestValues)
			if seen[key] {
				continue
			}
			seen[key] = true
			report, err := run(candidate.Program, bestValues, r.SearchSeeds)
			if err != nil {
				issue(err)
				continue
			}
			tradeoff(report)
			if best == nil || nativeSafetyNotWorse(report, best) && (report.MeanDPS > best.MeanDPS || report.MeanDPS == best.MeanDPS && candidate.Program.StructuralComplexity() < bestProgram.StructuralComplexity()) {
				best = report
				bestProgram = candidate.Program
				bestEdits = append(append([]nativeflow.StructuralEdit(nil), parentEdits...), candidate.Edit)
			}
		}
		for i := range bestValues {
			for _, delta := range []float64{-.25, .25} {
				if result.Evaluations >= searchLimit || ctx.Err() != nil {
					break
				}
				candidate := append([]nativeflow.Parameter(nil), bestValues...)
				value := math.Round((candidate[i].Value+delta)*1e6) / 1e6
				if value < candidate[i].Min || value > candidate[i].Max {
					continue
				}
				candidate[i].Value = value
				key := signature(bestProgram, candidate)
				if seen[key] {
					continue
				}
				seen[key] = true
				report, err := run(bestProgram, candidate, r.SearchSeeds)
				if err != nil {
					issue(err)
					continue
				}
				tradeoff(report)
				if best == nil || report.MeanDPS > best.MeanDPS && nativeSafetyNotWorse(report, best) {
					best = report
					bestValues = candidate
				}
			}
		}
		if before == result.Evaluations || result.Evaluations >= searchLimit || ctx.Err() != nil {
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
	validated, err := run(bestProgram, bestValues, r.ValidationSeeds)
	var reference *engine.Report
	if baseErr == nil {
		if signature(bestProgram, bestValues) == signature(r.Base.NativeFlow, original) {
			if err == nil {
				reference = validated
			}
		} else {
			reference, _ = run(r.Base.NativeFlow, original, r.ValidationSeeds)
		}
	}
	if err != nil {
		issue(err)
		if reference == nil {
			result.Status = "validation_failed"
			return result, nil
		}
		validated = reference
		bestValues = original
		bestEdits = nil
		bestProgram = r.Base.NativeFlow
	}
	result.Baseline = reference
	result.Report = validated
	if reference != nil && (validated.MeanDPS < reference.MeanDPS || validated.MeanDPS == reference.MeanDPS && len(bestEdits) == 0 || !nativeSafetyNotWorse(validated, reference)) {
		result.Report = reference
		bestValues = original
		bestEdits = nil
		bestProgram = r.Base.NativeFlow
		result.Status = "feasible_baseline"
	} else {
		result.Status = "feasible_recommendation"
		if reference != nil && validated.MeanDPS == reference.MeanDPS {
			result.Status = "feasible_simplification"
		}
		if reference != nil && result.Status != "feasible_simplification" {
			result.Improvement = optimizer.CompareImprovement("balanced", map[string]float64{"rotation": 1}, &optimizer.Plan{Reports: map[string]engine.Report{"rotation": *validated}}, &optimizer.Plan{Reports: map[string]engine.Report{"rotation": *reference}}, len(r.ValidationSeeds))
			if result.Improvement.State != "confirmed" {
				result.Status = "feasible_uncertain"
			}
		}
	}
	changed := len(bestEdits) > 0
	for _, v := range bestValues {
		changed = changed || v.Value != v.Original
	}
	if changed {
		for _, kind := range []string{"input_delay_200ms", "drop_first_burst"} {
			baseProgram := *r.Base.NativeFlow
			candidateProgram := *bestProgram
			if kind == "input_delay_200ms" {
				baseProgram.InputDelayFrames = 12
				candidateProgram.InputDelayFrames = 12
			} else {
				baseProgram.DropFirstBurst = true
				candidateProgram.DropFirstBurst = true
			}
			seeds := []int64{r.ValidationSeeds[0]}
			baseProbe, baseError := run(&baseProgram, original, seeds)
			candidateProbe, candidateError := run(&candidateProgram, bestValues, seeds)
			probe := NativeProbe{Kind: kind, Samples: 1}
			if baseProbe != nil {
				probe.Baseline = baseProbe.NativeQuality
			}
			if candidateProbe != nil {
				probe.Candidate = candidateProbe.NativeQuality
			}
			if baseError != nil {
				probe.BaselineError = baseError.Error()
			}
			if candidateError != nil {
				probe.CandidateError = candidateError.Error()
			}
			probe.Passed = candidateError == nil && (baseError != nil || nativeSafetyNotWorse(candidateProbe, baseProbe))
			result.NativeProbes = append(result.NativeProbes, probe)
			if !probe.Passed {
				result.Issues = append(result.Issues, "候选未通过独立受控扰动非退化检查："+kind)
				bestValues = original
				bestEdits = nil
				if reference != nil {
					result.Report = reference
					result.Status = "feasible_baseline"
				} else {
					result.Report = nil
					result.Status = "validation_failed"
				}
				break
			}
		}
	}
	for _, v := range bestValues {
		if v.Value != v.Original {
			result.NativeChanges = append(result.NativeChanges, v)
		}
	}
	result.NativeEdits = bestEdits
	return result, nil
}

func nativeSafetyNotWorse(candidate, baseline *engine.Report) bool {
	a, b := candidate.NativeQuality, baseline.NativeQuality
	if a == nil || b == nil || !a.Complete || !b.Complete || a.Samples != b.Samples {
		return false
	}
	return a.MinShieldCoverage+1e-9 >= b.MinShieldCoverage && a.MinCriticalShieldCoverage+1e-9 >= b.MinCriticalShieldCoverage && a.MaxFailedRounds <= b.MaxFailedRounds && a.MaxDamageGapSeconds <= b.MaxDamageGapSeconds+1e-9 && a.MinPartyHP+1e-9 >= b.MinPartyHP
}

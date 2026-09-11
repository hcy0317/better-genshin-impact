package rotation

import (
	"context"
	"errors"
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
	"math"
	"regexp"
	"strings"
)

const Marker = "__BETTERGI_ROTATION__"

type Action struct {
	Character string  `json:"character"`
	Kind      string  `json:"kind"`
	Seconds   float64 `json:"seconds,omitempty"`
}
type Request struct {
	Base            engine.Request `json:"base"`
	Actions         []Action       `json:"actions"`
	SearchSeeds     []int64        `json:"searchSeeds"`
	ValidationSeeds []int64        `json:"validationSeeds"`
	Budget          int            `json:"budget"`
}
type Result struct {
	NativeTradeoffs []NativeTradeoff            `json:"nativeTradeoffs,omitempty"`
	NativeProbes    []NativeProbe               `json:"nativeProbes,omitempty"`
	Native          bool                        `json:"native,omitempty"`
	NativeChanges   []nativeflow.Parameter      `json:"nativeChanges,omitempty"`
	NativeEdits     []nativeflow.StructuralEdit `json:"nativeEdits,omitempty"`
	Status          string                      `json:"status"`
	Actions         []Action                    `json:"actions,omitempty"`
	Script          string                      `json:"script,omitempty"`
	Report          *engine.Report              `json:"report,omitempty"`
	Baseline        *engine.Report              `json:"baseline,omitempty"`
	Evaluations     int                         `json:"evaluations"`
	Improvement     *optimizer.Improvement      `json:"improvement,omitempty"`
	Issues          []string                    `json:"issues"`
}

type NativeProbe struct {
	Passed         bool                  `json:"passed"`
	Kind           string                `json:"kind"`
	Samples        int                   `json:"samples"`
	Baseline       *engine.NativeQuality `json:"baseline,omitempty"`
	Candidate      *engine.NativeQuality `json:"candidate,omitempty"`
	BaselineError  string                `json:"baselineError,omitempty"`
	CandidateError string                `json:"candidateError,omitempty"`
}

type NativeTradeoff struct {
	DPS         float64               `json:"dps"`
	BaselineDPS float64               `json:"baselineDps"`
	Quality     *engine.NativeQuality `json:"quality"`
	Reason      string                `json:"reason"`
}

// Script is a finite-action IR rendering, not a general parser for arbitrary
// community programs. Time-bounded normal strings finish the last native attack.
func Script(actions []Action) (string, error) {
	if len(actions) == 0 || len(actions) > 80 {
		return "", errors.New("rotation requires 1..80 declared actions")
	}
	var body strings.Builder
	for i, a := range actions {
		if !regexp.MustCompile(`^[a-z][a-z0-9]{0,49}$`).MatchString(a.Character) {
			return "", errors.New("invalid rotation character")
		}
		if math.IsNaN(a.Seconds) || math.IsInf(a.Seconds, 0) || a.Seconds < 0 || a.Seconds > 30 {
			return "", errors.New("invalid declared action duration")
		}
		switch a.Kind {
		case "skill", "burst":
			if a.Seconds != 0 {
				return "", errors.New("skill/burst duration belongs to native gcsim")
			}
			fmt.Fprintf(&body, "%s %s;\n", a.Character, a.Kind)
		case "wait":
			if int(math.Round(a.Seconds*60)) < 1 {
				return "", errors.New("wait must advance time")
			}
			fmt.Fprintf(&body, "wait(%d);\n", int(math.Round(a.Seconds*60)))
		case "attack_seconds":
			if int(math.Round(a.Seconds*60)) < 1 {
				return "", errors.New("attack duration must be positive")
			}
			fmt.Fprintf(&body, "let bgi_start_%d = f(); while f() < bgi_start_%d + %d { %s attack; }\n", i, i, int(math.Round(a.Seconds*60)), a.Character)
		default:
			return "", fmt.Errorf("unsupported rotation IR action: %s", a.Kind)
		}
	}
	return "active " + actions[0].Character + ";\nwhile 1 {\n" + body.String() + "}\n", nil
}

func Optimize(ctx context.Context, r Request, evaluate optimizer.Evaluator) (Result, error) {
	if r.Base.NativeFlow != nil {
		return optimizeNative(ctx, r, evaluate)
	}
	result := Result{Issues: []string{}}
	if strings.Count(r.Base.Config, Marker) != 1 || r.Budget < 4 || r.Budget > 512 || evaluate == nil {
		return result, errors.New("invalid rotation template or evaluation budget")
	}
	if len(r.SearchSeeds) == 0 || len(r.SearchSeeds) > engine.MaxEvaluationSamples || len(r.ValidationSeeds) == 0 || len(r.ValidationSeeds) > engine.MaxEvaluationSamples {
		return result, errors.New("declared search and independent validation batches required")
	}
	seenSeeds := map[int64]bool{}
	for _, batch := range [][]int64{r.SearchSeeds, r.ValidationSeeds} {
		for _, seed := range batch {
			if seenSeeds[seed] {
				return result, errors.New("overlapping/duplicate rotation seeds")
			}
			seenSeeds[seed] = true
		}
	}
	baseScript, err := Script(r.Actions)
	if err != nil {
		return result, err
	}
	run := func(script string, seeds []int64) (*engine.Report, error) {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		req := r.Base
		req.CompactSamples = true
		req.SchemaVersion = "1"
		req.EngineRevision = engine.Revision
		req.Seeds = seeds
		req.Config = strings.Replace(req.Config, Marker, script, 1)
		report, err := evaluate(ctx, req)
		result.Evaluations++
		if err != nil {
			return nil, err
		}
		if !report.Validation.Complete || report.Validation.State != "passed" {
			return &report, errors.New("rotation trajectory constraints did not pass")
		}
		return &report, nil
	}
	base, baseErr := run(baseScript, r.SearchSeeds)
	result.Baseline = base
	var best *engine.Report
	bestActions := append([]Action(nil), r.Actions...)
	bestScript := baseScript
	if baseErr == nil {
		best = base
	} else {
		result.Issues = append(result.Issues, baseErr.Error())
	}
	seen := map[string]bool{baseScript: true}
	consider := func(actions []Action) {
		if result.Evaluations >= r.Budget-2 || ctx.Err() != nil {
			return
		}
		script, err := Script(actions)
		if err != nil || seen[script] {
			return
		}
		seen[script] = true
		report, err := run(script, r.SearchSeeds)
		if err != nil {
			if len(result.Issues) < 8 {
				result.Issues = append(result.Issues, err.Error())
			}
			return
		}
		if best == nil || report.MeanDPS > best.MeanDPS {
			best = report
			bestScript = script
			bestActions = append([]Action(nil), actions...)
		}
	}
	for pass := 0; pass < r.Budget; pass++ {
		before := result.Evaluations
		for i := range bestActions {
			if i+1 < len(bestActions) {
				candidate := append([]Action(nil), bestActions...)
				candidate[i], candidate[i+1] = candidate[i+1], candidate[i]
				consider(candidate)
			}
			if bestActions[i].Kind == "attack_seconds" || bestActions[i].Kind == "wait" {
				for _, delta := range []float64{-.25, .25} {
					candidate := append([]Action(nil), bestActions...)
					candidate[i].Seconds += delta
					consider(candidate)
				}
			}
		}
		if ctx.Err() != nil || result.Evaluations >= r.Budget-2 || result.Evaluations == before {
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
	validated, err := run(bestScript, r.ValidationSeeds)
	var baseline *engine.Report
	if baseErr == nil {
		if b, e := run(baseScript, r.ValidationSeeds); e == nil {
			baseline = b
		}
	}
	result.Baseline = baseline
	if err != nil {
		if baseline == nil {
			result.Status = "validation_failed"
			return result, nil
		}
		validated = baseline
		bestActions = append([]Action(nil), r.Actions...)
		bestScript = baseScript
	}
	if baseline != nil && validated.MeanDPS <= baseline.MeanDPS {
		validated = baseline
		bestActions = append([]Action(nil), r.Actions...)
		bestScript = baseScript
		result.Status = "feasible_baseline"
	} else {
		result.Status = "feasible_recommendation"
		if baseline != nil {
			result.Improvement = optimizer.CompareImprovement("balanced", map[string]float64{"rotation": 1}, &optimizer.Plan{Reports: map[string]engine.Report{"rotation": *validated}}, &optimizer.Plan{Reports: map[string]engine.Report{"rotation": *baseline}}, len(r.ValidationSeeds))
			if result.Improvement.State != "confirmed" {
				result.Status = "feasible_uncertain"
			}
		}
	}
	result.Actions, result.Script, result.Report = bestActions, bestScript, validated
	return result, nil
}

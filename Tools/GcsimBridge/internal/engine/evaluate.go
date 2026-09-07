// Package engine is the worker-only adapter for the pinned upstream simulator.
// Untrusted configurations must be evaluated by the bounded process supervisor.
package engine

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math"

	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/gcs/ast"
	"github.com/genshinsim/gcsim/pkg/gcs/eval"
	"github.com/genshinsim/gcsim/pkg/gcs/parser"
	resultinfo "github.com/genshinsim/gcsim/pkg/result"
	"github.com/genshinsim/gcsim/pkg/simulation"
	"github.com/genshinsim/gcsim/pkg/stats"
)

// Official packaging overrides this together with the exact SDK version; the
// executable verifies both against Go's checksummed build dependency metadata.
var Revision = "1de5a42438791757a7178b16e59ec97dc1690d61"

const AdapterVersion = "1"

type Request struct {
	Assumptions        []string              `json:"assumptions,omitempty"`
	AutoRounds         bool                  `json:"autoRounds,omitempty"`
	MainLoopIndex      int                   `json:"mainLoopIndex,omitempty"`
	RoundWarmup        int                   `json:"roundWarmup,omitempty"`
	RotationLineOffset int                   `json:"rotationLineOffset,omitempty"`
	SchemaVersion      string                `json:"schemaVersion"`
	EngineRevision     string                `json:"engineRevision"`
	Config             string                `json:"config"`
	Seeds              []int64               `json:"seeds"`
	Inventory          *InventoryRef         `json:"inventory,omitempty"`
	Equipment          map[string][]Artifact `json:"equipment,omitempty"`
	Buffs              []Buff                `json:"buffs,omitempty"`
	Rounds             []Round               `json:"rounds,omitempty"`
	Constraints        []Constraint          `json:"constraints,omitempty"`
	AllowPartial       bool                  `json:"allowPartial,omitempty"`
}

// InventoryRef refers to the existing scan; this adapter does not own a second inventory.
type InventoryRef struct {
	UID            string `json:"uid"`
	ScanSessionID  string `json:"scanSessionId"`
	CatalogVersion string `json:"catalogVersion"`
	SnapshotDigest string `json:"snapshotDigest"`
}

// MainStatValue is resolved by the caller's versioned catalog. The scanner currently
// supplies the main-stat key and artifact level, not its numeric value.
type Artifact struct {
	ScanIndex     int       `json:"scanIndex"`
	SlotKey       string    `json:"slotKey"`
	SetKey        string    `json:"setKey"`
	MainStatKey   string    `json:"mainStatKey"`
	MainStatValue *float64  `json:"mainStatValue"`
	Substats      []Substat `json:"substats"`
}

type Substat struct {
	Key     string  `json:"key"`
	Value   float64 `json:"value"`
	Dormant bool    `json:"dormant,omitempty"`
}

type Report struct {
	SamplingIterations    int                           `json:"samplingIterations"`
	SamplesCompacted      bool                          `json:"samplesCompacted,omitempty"`
	SampleMetrics         []SampleMetricEvidence        `json:"sampleMetrics,omitempty"`
	RoundTraces           []RoundTrace                  `json:"roundTraces,omitempty"`
	RoundMetricsAvailable bool                          `json:"roundMetricsAvailable"`
	MetricIssues          []string                      `json:"metricIssues,omitempty"`
	StopMode              string                        `json:"stopMode"`
	EngineRevision        string                        `json:"engineRevision"`
	AdapterVersion        string                        `json:"adapterVersion"`
	Samples               []stats.Result                `json:"samples"`
	MeanDPS               float64                       `json:"meanDps"`
	ScoredDPS             []float64                     `json:"scoredDps"`
	BuffActivations       [][]BuffActivation            `json:"buffActivations"`
	Assumptions           []string                      `json:"assumptions"`
	Validation            Validation                    `json:"validation"`
	InputSHA256           string                        `json:"inputSha256"`
	Parameters            *info.ActionList              `json:"parameters"`
	DurationSeconds       float64                       `json:"durationSeconds"`
	ScoringWindows        []Round                       `json:"scoringWindows,omitempty"`
	MeanDPSSource         string                        `json:"meanDpsSource"`
	Metrics               []Metric                      `json:"metrics"`
	ManualBuffs           []Buff                        `json:"manualBuffs,omitempty"`
	IncompleteCharacters  []string                      `json:"incompleteCharacters,omitempty"`
	Support               string                        `json:"support"`
	Inventory             *InventoryRef                 `json:"inventory,omitempty"`
	EnergyWindows         [][]EnergyObservation         `json:"energyWindows"`
	InitialStats          map[string]map[string]float64 `json:"initialStats"`
}

// Evaluate is called only inside an owned, resource-limited worker process.
func Evaluate(request Request) (Report, error) {
	report := Report{EngineRevision: Revision, AdapterVersion: AdapterVersion, Support: "native_simulation"}
	report.SamplingIterations = len(request.Seeds)
	if len(request.Assumptions) > 16 {
		return report, errors.New("too many declared assumptions")
	}
	for _, assumption := range request.Assumptions {
		if assumption != "auxiliary_logic_disabled" {
			return report, errors.New("unknown declared assumption")
		}
		report.Assumptions = append(report.Assumptions, assumption)
	}
	if request.SchemaVersion != "1" || request.EngineRevision != Revision {
		return report, errors.New("unsupported request schema or engine revision")
	}
	if len(request.Config) == 0 || len(request.Config) > 1024*1024 || len(request.Seeds) == 0 || len(request.Seeds) > MaxEvaluationSamples {
		return report, errors.New("one bounded configuration and 1..1000 declared seeds are required")
	}
	seen := make(map[int64]bool, len(request.Seeds))
	for _, seed := range request.Seeds {
		if seen[seed] {
			return report, errors.New("duplicate validation seed")
		}
		seen[seed] = true
	}
	file := ast.NewFile()
	cfg, script, err := parser.New(file, request.Config).Parse()
	if err != nil {
		return report, fmt.Errorf("parse gcsim configuration: %w", err)
	}
	if len(cfg.Errors) != 0 {
		return report, fmt.Errorf("invalid gcsim configuration: %w", errors.Join(cfg.Errors...))
	}
	for _, profile := range cfg.Characters {
		if !resultinfo.IsCharacterComplete(profile.Base.Key) {
			report.IncompleteCharacters = append(report.IncompleteCharacters, profile.Base.Key.String())
			report.Assumptions = append(report.Assumptions, "gcsim_incomplete:"+profile.Base.Key.String())
		}
	}
	if len(report.IncompleteCharacters) > 0 && !request.AllowPartial {
		return report, errors.New("scenario includes incomplete upstream character support; explicit trial opt-in is required")
	}
	if request.Inventory != nil {
		ref := *request.Inventory
		report.Inventory = &ref
	}
	if err := applyEquipment(cfg, request); err != nil {
		return report, err
	}
	if request.AutoRounds && (len(request.Rounds) > 0 || request.RoundWarmup < 0 || request.RoundWarmup > 63) {
		return report, errors.New("automatic rounds cannot be combined with legacy fixed windows or invalid warmup counts")
	}
	buffs, err := prepareBuffs(cfg, request.Buffs, request.Rounds, request.AutoRounds)
	if err != nil {
		return report, err
	}
	for _, buff := range buffs {
		report.Assumptions = append(report.Assumptions, "manual_buff:"+buff.ID+":"+buff.Relationship)
		if buff.CoverageRatio != nil {
			report.Assumptions = append(report.Assumptions, "coverage_ratio_not_damage_share:"+buff.ID)
		}
	}
	report.ManualBuffs = buffs
	if math.IsNaN(cfg.Settings.Duration) || math.IsInf(cfg.Settings.Duration, 0) || cfg.Settings.Duration < 0 || cfg.Settings.Duration > 600 || (!cfg.Settings.DamageMode && int(cfg.Settings.Duration*60)<1) {
		return report, errors.New("fixed-duration evaluation requires 0 < duration <= 600 seconds; target/script mode requires valid target HP")
	}
	report.StopMode = "fixed_duration"
	if cfg.Settings.DamageMode {
		report.StopMode = "target_or_script"
		cfg.Settings.Duration = 0
	}
	if len(request.Constraints) > 64 || (len(request.Constraints) > 0 && len(request.Rounds) == 0 && !request.AutoRounds) {
		return report, errors.New("constraints require explicit scoring windows and a bounded list")
	}
	constraintIDs := make(map[string]bool)
	for _, constraint := range request.Constraints {
		if constraint.ID == "" || constraintIDs[constraint.ID] || math.IsNaN(constraint.Threshold) || math.IsInf(constraint.Threshold, 0) || constraint.Threshold < 0 {
			return report, errors.New("invalid or duplicate constraint identity/threshold")
		}
		constraintIDs[constraint.ID] = true
	}
	rounds := request.Rounds
	if len(rounds) == 0 && !cfg.Settings.DamageMode {
		rounds = []Round{{ID: "native-full", StartFrame: 0, EndFrame: int(cfg.Settings.Duration * 60)}}
	}
	if err := validateRounds(rounds, int(cfg.Settings.Duration*60)); err != nil && len(rounds) > 0 {
		return report, err
	}
	if cfg.Settings.IgnoreBurstEnergy {
		report.Assumptions = append(report.Assumptions, "ignore_burst_energy")
	}
	if cfg.EnergySettings.Active {
		report.Assumptions = append(report.Assumptions, "external_energy_schedule")
	}
	if len(report.Assumptions) > 0 {
		report.Support = "trial"
	}
	encoded, err := json.Marshal(request)
	if err != nil {
		return report, err
	}
	digest := sha256.Sum256(encoded)
	report.InputSHA256 = hex.EncodeToString(digest[:])
	report.DurationSeconds = cfg.Settings.Duration
	report.ScoringWindows = append([]Round(nil), request.Rounds...)
	report.MeanDPSSource = "gcsim.Result.DPS"
	// Sampling and parallelism belong to the caller's bounded job, not embedded scripts.
	cfg.Settings.NumberOfWorkers = 1
	cfg.Settings.Iterations = 1
	cfg.Settings.CollectStats = nil
	report.Parameters = cfg.Copy()
	var variableValidations []Validation
	var automaticValidations []Validation
	var automaticWindows [][]Round
	for _, seed := range request.Seeds {
		copy := cfg.Copy()
		core, err := simulation.NewCore(seed, false, copy)
		if err != nil {
			return report, err
		}
		program := script.Copy()
		var roundRecorder *roundObserver
		if request.AutoRounds {
			program, roundRecorder, err = observeAutomaticRounds(script, file, core, seed, request.MainLoopIndex, request.RotationLineOffset)
			if err != nil {
				return report, err
			}
		}
		evaluator, err := eval.NewEvaluator(file, program, core)
		if err != nil {
			return report, err
		}
		sim, err := simulation.New(copy, evaluator, core)
		if err != nil {
			return report, err
		}
		if report.InitialStats == nil {
			report.InitialStats = initialStats(core)
		}
		horizon := int(cfg.Settings.Duration * 60)
		if cfg.Settings.DamageMode {
			horizon = math.MaxInt32
		}
		activations := attachBuffs(core, buffs, request.Rounds, horizon)
		if roundRecorder != nil {
			for _, buff := range buffs {
				if buff.Anchor == "round" && roundRecorder.trace.State != "complete" {
					return report, errors.New("round-anchored buffs require an unambiguous observable main loop")
				}
			}
			roundRecorder.onStart = func() {
				for _, buff := range buffs {
					if buff.Anchor == "round" {
						applyBuffEffect(core, buff, horizon)
						expires := -1
						if buff.DurationFrames >= 0 {
							expires = core.F + buff.DurationFrames
						}
						*activations = append(*activations, BuffActivation{ID: buff.ID, Frame: core.F, ExpiresAt: expires})
					}
				}
			}
			if roundRecorder.linear {
				roundRecorder.onStart()
			}
		}
		energyWindows := &[]EnergyObservation{}
		if !request.AutoRounds {
			energyWindows = observeRoundEnergy(core, rounds)
		}
		scoredDamage := 0.0
		if len(request.Rounds) > 0 {
			core.Events.Subscribe(event.OnEnemyDamage, func(args ...any) {
				if insideWindows(core.F, request.Rounds) {
					scoredDamage += args[2].(float64)
				}
			}, "bettergi/scored-team-damage")
		}
		result, err := sim.Run()
		if err != nil {
			return report, err
		}
		if result.Duration <= 0 || math.IsNaN(result.DPS) || math.IsInf(result.DPS, 0) {
			return report, errors.New("gcsim returned an invalid trajectory")
		}
		if len(request.Seeds) > 64 {
			trimFrameVectors(&result)
		}
		report.Samples = append(report.Samples, result)
		report.BuffActivations = append(report.BuffActivations, *activations)
		if roundRecorder != nil {
			trace := roundRecorder.finish(result.Duration)
			report.RoundTraces = append(report.RoundTraces, trace)
			windows, roundErr := traceWindows(trace, request.RoundWarmup)
			automaticWindows = append(automaticWindows, windows)
			if roundErr != nil {
				report.MetricIssues = append(report.MetricIssues, "样本 "+trace.Seed+"："+roundErr.Error())
			}
			for _, round := range trace.Rounds {
				for _, boundary := range []string{"start", "end"} {
					frame, energy := round.StartFrame, round.StartEnergy
					if boundary == "end" {
						frame, energy = round.EndFrame, round.EndEnergy
					}
					*energyWindows = append(*energyWindows, EnergyObservation{Round: round.ID, Frame: frame, Boundary: boundary, Phase: "actual_script_boundary", Energy: energy})
				}
			}
			if len(request.Constraints) > 0 {
				if roundErr != nil {
					automaticValidations = append(automaticValidations, Validation{State: "indeterminate", Complete: false, Statement: "finite_declared_batch_only", Checks: []Check{{Seed: trace.Seed, Constraint: "round_coverage", State: "indeterminate", Source: "actual_script_boundary", Reason: roundErr.Error()}}})
				} else {
					automaticValidations = append(automaticValidations, ValidateBatch([]int64{seed}, windows, request.Constraints, []stats.Result{result}))
				}
			}
		}
		report.EnergyWindows = append(report.EnergyWindows, *energyWindows)
		dps := result.DPS
		if len(request.Rounds) > 0 {
			frames := 0
			for _, round := range request.Rounds {
				frames += round.EndFrame - round.StartFrame
			}
			dps = scoredDamage * 60 / float64(frames)
			report.MeanDPSSource = "gcsim.OnEnemyDamage/scored_window_seconds"
		}
		report.MeanDPS += dps / float64(len(request.Seeds))
		report.ScoredDPS = append(report.ScoredDPS, dps)
		if cfg.Settings.DamageMode {
			report.DurationSeconds += float64(result.Duration) / 60 / float64(len(request.Seeds))
		}
		if len(rounds) == 0 {
			variableValidations = append(variableValidations, ValidateBatch([]int64{seed}, []Round{{ID: "native-full", StartFrame: 0, EndFrame: result.Duration}}, request.Constraints, []stats.Result{result}))
		}
	}
	report.Validation = ValidateBatch(request.Seeds, rounds, request.Constraints, report.Samples)
	if len(rounds) == 0 {
		report.Validation = mergeValidations(variableValidations)
	}
	if request.AutoRounds && len(request.Constraints) > 0 {
		report.Validation = mergeValidations(automaticValidations)
	}
	if request.AutoRounds {
		report.Metrics, err = SummarizeAutomaticMetrics(report.Samples, automaticWindows)
		report.RoundMetricsAvailable = err == nil
		if err != nil {
			report.MetricIssues = append(report.MetricIssues, err.Error())
			report.Metrics = nil
		}
		compactLargeSamples(&report, request, automaticWindows)
		return report, nil
	}
	report.Metrics, err = SummarizeMetrics(report.Samples, request.Rounds)
	if err != nil {
		return report, err
	}
	compactLargeSamples(&report, request, nil)
	return report, nil
}

func mergeValidations(values []Validation) Validation {
	result := Validation{State: "passed", Complete: true, Statement: "finite_declared_batch_only", Checks: []Check{}}
	if len(values) == 0 {
		result.State = "indeterminate"
		result.Complete = false
		return result
	}
	for _, value := range values {
		result.Checks = append(result.Checks, value.Checks...)
		result.Complete = result.Complete && value.Complete
		if value.State == "failed" || (value.State == "indeterminate" && result.State != "failed") {
			result.State = value.State
		}
	}
	return result
}

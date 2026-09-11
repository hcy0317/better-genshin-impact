// Package engine is the worker-only adapter for the pinned upstream simulator.
// Untrusted configurations must be evaluated by the bounded process supervisor.
package engine

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"github.com/genshinsim/gcsim/pkg/core/action"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"math"
	"regexp"

	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/gcs/ast"
	"github.com/genshinsim/gcsim/pkg/gcs/eval"
	"github.com/genshinsim/gcsim/pkg/gcs/parser"
	"github.com/genshinsim/gcsim/pkg/simulation"
	"github.com/genshinsim/gcsim/pkg/stats"
)

// Official packaging overrides this together with the exact SDK version; the
// executable verifies both against Go's checksummed build dependency metadata.
var Revision = "720f1a1f81673f9dc82f803e32239c4ad729bd0c"

const AdapterVersion = "4"

var weaponWaitAdaptation = regexp.MustCompile(`^removed_impossible_favonius_wait:[a-z0-9]{1,40}$`)

type Request struct {
	RoundCount         int                   `json:"roundCount,omitempty"`
	NativeFlow         *nativeflow.Program   `json:"nativeFlow,omitempty"`
	CompactSamples     bool                  `json:"compactSamples,omitempty"`
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
	RequestedRoundCount   int                           `json:"requestedRoundCount,omitempty"`
	TerminalFlushFrames   int                           `json:"terminalFlushFrames,omitempty"`
	NativeQuality         *NativeQuality                `json:"nativeQuality,omitempty"`
	NativeFlowTraces      []NativeFlowTrace             `json:"nativeFlowTraces,omitempty"`
	Warnings              []string                      `json:"warnings,omitempty"`
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
type NativeFlowTrace struct {
	Seed      int64                   `json:"seed"`
	Events    []nativeflow.TraceEvent `json:"events"`
	Truncated bool                    `json:"truncated"`
}

// Evaluate is called only inside an owned, resource-limited worker process.
func Evaluate(request Request) (Report, error) {
	report := Report{EngineRevision: Revision, AdapterVersion: AdapterVersion, Support: "native_simulation"}
	if request.RoundCount < 0 || request.RoundCount > 64 || request.RoundCount > 0 && (!request.AutoRounds || request.RoundWarmup >= request.RoundCount || len(request.Rounds) > 0) {
		return report, errors.New("round_count_invalid：循环次数须为1至64，开启自动轮次，且大于忽略的开场轮数")
	}
	report.RequestedRoundCount = request.RoundCount
	report.SamplingIterations = len(request.Seeds)
	if request.NativeFlow != nil {
		if err := request.NativeFlow.Validate(); err != nil {
			return report, err
		}
		report.Assumptions = append(report.Assumptions, "native_flow_model")
		if request.NativeFlow.InputDelayFrames > 0 {
			report.Assumptions = append(report.Assumptions, "native_diagnostic_input_delay")
		}
		if request.NativeFlow.DropFirstBurst {
			report.Assumptions = append(report.Assumptions, "native_diagnostic_first_burst_drop")
		}
		nodes := append([]nativeflow.Node(nil), request.NativeFlow.Root...)
		for _, b := range request.NativeFlow.Blocks {
			nodes = append(nodes, b.Nodes...)
		}
		movement, charge := false, false
		for _, n := range nodes {
			movement = movement || n.Kind == "walk" || n.Kind == "dash"
			charge = charge || n.Kind == "charge"
		}
		if movement {
			report.Assumptions = append(report.Assumptions, "native_movement_timing_only")
		}
		if charge {
			report.Assumptions = append(report.Assumptions, "native_standard_charge")
		}
		for _, block := range request.NativeFlow.Blocks {
			if block.Macro == "neuvillette_charge_v1" {
				report.Assumptions = append(report.Assumptions, "native_macro_neuvillette_charge")
				break
			}
		}
	}
	if len(request.Assumptions) > 16 {
		return report, errors.New("too many declared assumptions")
	}
	for _, assumption := range request.Assumptions {
		if assumption != "auxiliary_logic_disabled" && !weaponWaitAdaptation.MatchString(assumption) {
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
	if request.NativeFlow != nil {
		block, ok := script.(*ast.BlockStmt)
		if !ok || len(block.List) > 0 {
			return report, errors.New("原生流程不能混合gcsim可执行语句；请明确切换循环来源")
		}
	}
	report.Warnings = scriptWarnings(script, file, cfg, request.RotationLineOffset)
	for _, profile := range cfg.Characters {
		if !characterComplete(profile.Base.Key) {
			report.IncompleteCharacters = append(report.IncompleteCharacters, profile.Base.Key.String())
			prefix := "gcsim_incomplete:"
			if profile.Base.Key.String() == "sandrone" && SupplementalCharacterRevision != "" {
				prefix = "supplemental_incomplete:"
			}
			report.Assumptions = append(report.Assumptions, prefix+profile.Base.Key.String())
		}
	}
	if len(report.IncompleteCharacters) > 0 && !request.AllowPartial {
		return report, errors.New("scenario includes incomplete character support; explicit trial opt-in is required")
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
	if request.RoundCount > 0 {
		// Non-lethal training targets: enemy HP is not an execution deadline.
		cfg.Settings.DamageMode = false
		cfg.Settings.Duration = MaxTrajectorySeconds
	}
	if math.IsNaN(cfg.Settings.Duration) || math.IsInf(cfg.Settings.Duration, 0) || cfg.Settings.Duration < 0 || cfg.Settings.Duration > 600 || (!cfg.Settings.DamageMode && int(cfg.Settings.Duration*60) < 1) {
		return report, errors.New("fixed-duration evaluation requires 0 < duration <= 600 seconds; target/script mode requires valid target HP")
	}
	report.StopMode = "fixed_duration"
	if cfg.Settings.DamageMode {
		report.StopMode = "target_or_script"
		cfg.Settings.Duration = 0
	}
	if request.RoundCount > 0 {
		report.StopMode = "loop_count"
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
	if len(rounds) == 0 && !cfg.Settings.DamageMode && request.RoundCount == 0 {
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
	if request.RoundCount > 0 {
		report.DurationSeconds = 0
	}
	report.ScoringWindows = append([]Round(nil), request.Rounds...)
	report.MeanDPSSource = "gcsim.Result.DPS"
	// Sampling and parallelism belong to the caller's bounded job, not embedded scripts.
	cfg.Settings.NumberOfWorkers = 1
	cfg.Settings.Iterations = 1
	cfg.Settings.CollectStats = nil
	compact := request.CompactSamples || len(request.Seeds) > 64
	if compact {
		cfg.Settings.CollectStats = compactCollectorNames()
	}
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
		if request.AutoRounds && request.NativeFlow == nil {
			program, roundRecorder, err = observeAutomaticRounds(script, file, core, seed, request.MainLoopIndex, request.RotationLineOffset, request.RoundCount)
			if err != nil {
				return report, err
			}
		}
		var evaluator action.Evaluator
		var nativeEvaluator *nativeflow.Evaluator
		if request.NativeFlow != nil {
			nativeEvaluator, err = nativeflow.New(request.NativeFlow, core)
			evaluator = nativeEvaluator
			if err == nil && request.AutoRounds {
				nativeEvaluator.RoundLimit = request.RoundCount
				roundRecorder = newNativeRoundObserver(core, seed)
				nativeEvaluator.OnRound = roundRecorder.nativeBoundary
			}
		} else {
			evaluator, err = eval.NewEvaluator(file, program, core)
		}
		if err != nil {
			return report, err
		}
		if request.RoundCount > 0 && (roundRecorder == nil || roundRecorder.trace.State != "complete") {
			return report, errors.New("round_count_unavailable：无法确定主循环，请选择有效循环或修正无法划分轮次的控制语句")
		}
		exhaustion := &exhaustionObserver{Evaluator: evaluator}
		if request.RoundCount > 0 {
			evaluator = exhaustion
		}
		sim, err := simulation.New(copy, evaluator, core)
		if err != nil {
			return report, err
		}
		var activeFrames []int
		if compact {
			activeFrames = observeCompactActiveTime(core)
		}
		AttachEquipmentExtensions(core, request.Equipment)
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
		trajectoryLimitReached := false
		var quality *qualityObserver
		if nativeEvaluator != nil {
			quality = observeNativeQuality(core, nativeEvaluator)
		}
		if cfg.Settings.DamageMode || request.RoundCount > 0 {
			core.Events.Subscribe(event.OnTick, func(...any) {
				if request.RoundCount > 0 {
					count, _ := roundRecorder.countStatus()
					if count >= request.RoundCount || exhaustion.exhausted {
						// SDK checks equality after OnTick. The fractional frame avoids
						// floating-point round-down; this deadline comes from an actual
						// completed iteration, never from an estimated seconds-per-loop.
						copy.Settings.Duration = (float64(core.F) + 0.25) / 60
						return
					}
				}
				if core.F >= MaxTrajectorySeconds*60 {
					trajectoryLimitReached = true
					panic("owned trajectory resource limit")
				}
			}, "bettergi/trajectory-resource-limit")
		}
		result, err := sim.Run()
		if err != nil {
			_ = evaluator.Exit()
		}
		if compact {
			for i := range result.Characters {
				result.Characters[i].ActiveTime = activeFrames[i]
			}
		}
		if nativeEvaluator != nil && len(report.NativeFlowTraces) < 4 {
			report.NativeFlowTraces = append(report.NativeFlowTraces, NativeFlowTrace{seed, nativeEvaluator.Events, nativeEvaluator.TraceTruncated})
		}
		if trajectoryLimitReached {
			if request.RoundCount > 0 {
				count, _ := roundRecorder.countStatus()
				return report, fmt.Errorf("trajectory_limit：已完成%d/%d轮，当前循环在%d游戏秒保护上限内仍未结束；请检查等待条件或无进展分支", count, request.RoundCount, MaxTrajectorySeconds)
			}
			detail := ""
			if len(report.Warnings) > 0 {
				detail = "；" + report.Warnings[0]
			}
			return report, fmt.Errorf("trajectory_limit：单次战斗超过%d游戏秒的资源保护上限，未返回截断后的合格伤害；请检查循环中的等待条件或敌人血量%s", MaxTrajectorySeconds, detail)
		}
		if err != nil {
			return report, err
		}
		if request.RoundCount > 0 {
			count, complete := roundRecorder.countStatus()
			if count != request.RoundCount || !complete {
				return report, fmt.Errorf("round_count_incomplete：已记录%d/%d轮，但脚本提前结束或包含未完成/无时间推进的轮次", count, request.RoundCount)
			}
			// Whole-trajectory DPS includes the SDK's final one-frame flush;
			// per-round windows retain their actual pre-flush boundary.
			report.TerminalFlushFrames = 1
		}
		if result.Duration <= 0 || math.IsNaN(result.DPS) || math.IsInf(result.DPS, 0) {
			return report, errors.New("gcsim returned an invalid trajectory")
		}
		if quality != nil {
			mergeNativeQuality(&report, quality.finish())
		}
		if len(request.Seeds) > 64 || request.CompactSamples {
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
		if cfg.Settings.DamageMode || request.RoundCount > 0 {
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

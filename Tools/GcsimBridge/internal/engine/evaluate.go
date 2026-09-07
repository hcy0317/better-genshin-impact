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
	SchemaVersion  string                `json:"schemaVersion"`
	EngineRevision string                `json:"engineRevision"`
	Config         string                `json:"config"`
	Seeds          []int64               `json:"seeds"`
	Inventory      *InventoryRef         `json:"inventory,omitempty"`
	Equipment      map[string][]Artifact `json:"equipment,omitempty"`
	Buffs          []Buff                `json:"buffs,omitempty"`
	Rounds         []Round               `json:"rounds,omitempty"`
	Constraints    []Constraint          `json:"constraints,omitempty"`
	AllowPartial   bool                  `json:"allowPartial,omitempty"`
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
	EngineRevision       string                        `json:"engineRevision"`
	AdapterVersion       string                        `json:"adapterVersion"`
	Samples              []stats.Result                `json:"samples"`
	MeanDPS              float64                       `json:"meanDps"`
	ScoredDPS            []float64                     `json:"scoredDps"`
	BuffActivations      [][]BuffActivation            `json:"buffActivations"`
	Assumptions          []string                      `json:"assumptions"`
	Validation           Validation                    `json:"validation"`
	InputSHA256          string                        `json:"inputSha256"`
	Parameters           *info.ActionList              `json:"parameters"`
	DurationSeconds      float64                       `json:"durationSeconds"`
	ScoringWindows       []Round                       `json:"scoringWindows,omitempty"`
	MeanDPSSource        string                        `json:"meanDpsSource"`
	Metrics              []Metric                      `json:"metrics"`
	ManualBuffs          []Buff                        `json:"manualBuffs,omitempty"`
	IncompleteCharacters []string                      `json:"incompleteCharacters,omitempty"`
	Support              string                        `json:"support"`
	Inventory            *InventoryRef                 `json:"inventory,omitempty"`
	EnergyWindows        [][]EnergyObservation         `json:"energyWindows"`
	InitialStats         map[string]map[string]float64 `json:"initialStats"`
}

// Evaluate is called only inside an owned, resource-limited worker process.
func Evaluate(request Request) (Report, error) {
	report := Report{EngineRevision: Revision, AdapterVersion: AdapterVersion, Support: "native_simulation"}
	if request.SchemaVersion != "1" || request.EngineRevision != Revision {
		return report, errors.New("unsupported request schema or engine revision")
	}
	if len(request.Config) == 0 || len(request.Config) > 1024*1024 || len(request.Seeds) == 0 || len(request.Seeds) > 64 {
		return report, errors.New("one bounded configuration and 1..64 declared seeds are required")
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
	buffs, err := prepareBuffs(cfg, request.Buffs, request.Rounds)
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
	if math.IsNaN(cfg.Settings.Duration) || math.IsInf(cfg.Settings.Duration, 0) || cfg.Settings.Duration <= 0 || cfg.Settings.Duration > 600 || cfg.Settings.DamageMode {
		return report, errors.New("fixed-duration DPS evaluation requires 0 < duration <= 600 seconds")
	}
	if len(request.Constraints) > 64 || (len(request.Constraints) > 0 && len(request.Rounds) == 0) {
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
	if len(rounds) == 0 {
		rounds = []Round{{ID: "native-full", StartFrame: 0, EndFrame: int(cfg.Settings.Duration * 60)}}
	}
	if err := validateRounds(rounds, int(cfg.Settings.Duration*60)); err != nil {
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
	for _, seed := range request.Seeds {
		copy := cfg.Copy()
		core, err := simulation.NewCore(seed, false, copy)
		if err != nil {
			return report, err
		}
		evaluator, err := eval.NewEvaluator(file, script.Copy(), core)
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
		activations := attachBuffs(core, buffs, request.Rounds, int(cfg.Settings.Duration*60))
		energyWindows := observeRoundEnergy(core, rounds)
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
		report.Samples = append(report.Samples, result)
		report.BuffActivations = append(report.BuffActivations, *activations)
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
	}
	report.Validation = ValidateBatch(request.Seeds, rounds, request.Constraints, report.Samples)
	report.Metrics, err = SummarizeMetrics(report.Samples, request.Rounds)
	if err != nil {
		return report, err
	}
	return report, nil
}

package engine

import (
	"errors"
	"github.com/genshinsim/gcsim/pkg/stats"
	"math"
	"sort"
	"strconv"
)

type Round struct {
	ID         string `json:"id"`
	StartFrame int    `json:"startFrame"`
	EndFrame   int    `json:"endFrame"`
}

type Constraint struct {
	ID        string  `json:"id"`
	Kind      string  `json:"kind"`
	Character string  `json:"character"`
	Action    string  `json:"action,omitempty"`
	Threshold float64 `json:"threshold"`
}

type Check struct {
	Seed       string   `json:"seed"`
	Round      string   `json:"round"`
	Constraint string   `json:"constraint"`
	State      string   `json:"state"`
	Observed   *float64 `json:"observed,omitempty"`
	Source     string   `json:"source"`
	Unit       string   `json:"unit,omitempty"`
	Reason     string   `json:"reason,omitempty"`
}

type Validation struct {
	State     string  `json:"state"`
	Complete  bool    `json:"complete"`
	Statement string  `json:"statement"`
	Checks    []Check `json:"checks"`
}

func ValidateBatch(seeds []int64, rounds []Round, constraints []Constraint, samples []stats.Result) Validation {
	result := Validation{State: "passed", Complete: true, Statement: "finite_declared_batch_only", Checks: []Check{}}
	add := func(check Check) {
		result.Checks = append(result.Checks, check)
		if check.State == "failed" {
			result.State = "failed"
		} else if check.State == "indeterminate" && result.State != "failed" {
			result.State = "indeterminate"
		}
	}
	if len(seeds) == 0 || len(seeds) > 64 || validateRounds(rounds, 0) != nil {
		result.Complete = false
		add(Check{State: "indeterminate", Source: "batch", Reason: "empty or invalid declared validation batch"})
		return result
	}
	requested := make(map[uint64]bool)
	for _, seed := range seeds {
		if requested[uint64(seed)] {
			result.Complete = false
			add(Check{State: "indeterminate", Source: "batch", Reason: "duplicate declared seed"})
		}
		requested[uint64(seed)] = true
	}
	bySeed := make(map[uint64]stats.Result)
	for _, sample := range samples {
		_, duplicate := bySeed[sample.Seed]
		if !requested[sample.Seed] || duplicate {
			result.Complete = false
			add(Check{State: "indeterminate", Source: "batch", Reason: "unexpected or duplicate returned trajectory"})
		}
		bySeed[sample.Seed] = sample
	}
	for _, seed := range seeds {
		sample, present := bySeed[uint64(seed)]
		seedText := strconv.FormatInt(seed, 10)
		if !present {
			result.Complete = false
			add(Check{Seed: seedText, State: "indeterminate", Source: "batch", Reason: "missing declared trajectory"})
			continue
		}
		for _, round := range rounds {
			complete := sample.Duration >= round.EndFrame
			if !complete {
				result.Complete = false
			}
			if len(constraints) == 0 {
				state := "passed"
				if !complete {
					state = "indeterminate"
				}
				add(Check{Seed: seedText, Round: round.ID, Constraint: "coverage", State: state, Source: "gcsim.duration_frames", Unit: "frames"})
			}
			for _, constraint := range constraints {
				check := checkConstraint(sample, round, constraint, complete)
				check.Seed, check.Round, check.Constraint = seedText, round.ID, constraint.ID
				add(check)
			}
		}
	}
	return result
}

func validateRounds(rounds []Round, maximumFrame int) error {
	if len(rounds) == 0 || len(rounds) > 64 {
		return errors.New("1..64 scoring windows are required")
	}
	ordered := append([]Round(nil), rounds...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].StartFrame < ordered[j].StartFrame })
	ids := make(map[string]bool)
	end := 0
	for _, round := range ordered {
		if round.ID == "" || ids[round.ID] || round.StartFrame < end || round.EndFrame <= round.StartFrame || (maximumFrame > 0 && round.EndFrame > maximumFrame) {
			return errors.New("scoring windows must be identified, non-overlapping and inside the simulation")
		}
		ids[round.ID] = true
		end = round.EndFrame
	}
	return nil
}

func checkConstraint(sample stats.Result, round Round, constraint Constraint, complete bool) Check {
	check := Check{State: "indeterminate", Source: "unsupported_by_adapter", Reason: "metric is unavailable"}
	if constraint.ID == "" || constraint.Threshold < 0 || math.IsNaN(constraint.Threshold) || math.IsInf(constraint.Threshold, 0) {
		check.Reason = "invalid constraint"
		return check
	}
	var character *stats.CharacterResult
	for i := range sample.Characters {
		if sample.Characters[i].Name == constraint.Character {
			if character != nil {
				check.Reason = "duplicate character result"
				return check
			}
			character = &sample.Characters[i]
		}
	}
	if character == nil {
		check.Reason = "missing character trajectory"
		return check
	}
	value := 0.0
	maximum := false
	switch constraint.Kind {
	case "min_actions":
		if (constraint.Action != "burst" && constraint.Action != "skill" && constraint.Action != "attack") || math.Trunc(constraint.Threshold) != constraint.Threshold {
			check.Reason = "unsupported action/count"
			return check
		}
		check.Source, check.Unit = "gcsim.OnActionExec", "count"
		for _, action := range character.ActionEvents {
			if action.Action == constraint.Action && action.Frame >= round.StartFrame && action.Frame < round.EndFrame {
				value++
			}
		}
	case "max_failed_wait_frames":
		maximum = true
		check.Source, check.Unit = "gcsim.FailedActions", "frames"
		for _, wait := range character.FailedActions {
			if wait.Start < 0 || wait.End < wait.Start || wait.End > sample.Duration {
				check.Reason = "invalid failure interval"
				return check
			}
			value = max(value, float64(max(0, min(wait.End, round.EndFrame)-max(wait.Start, round.StartFrame))))
		}
	case "min_effective_healing":
		check.Source, check.Unit = "gcsim.HealEvents.Heal", "hp"
		for _, heal := range character.HealEvents {
			if math.IsNaN(heal.Heal) || math.IsInf(heal.Heal, 0) || heal.Heal < 0 {
				check.Reason = "invalid healing observation"
				return check
			}
			if heal.Frame >= round.StartFrame && heal.Frame < round.EndFrame {
				value += heal.Heal
			}
		}
	default:
		return check
	}
	check.Observed = &value
	check.Reason = ""
	if (maximum && value > constraint.Threshold) || (!maximum && complete && value < constraint.Threshold) {
		check.State = "failed"
	} else if complete {
		check.State = "passed"
	} else {
		check.Reason = "incomplete scoring window"
	}
	return check
}

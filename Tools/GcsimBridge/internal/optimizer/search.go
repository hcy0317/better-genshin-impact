package optimizer

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"sort"
	"strings"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

var slots = []string{"flower", "plume", "sands", "goblet", "circlet"}

type task struct {
	request          Request
	items            map[int]Item
	reserved         map[int]string
	scenarios        []Scenario
	weights          map[string]float64
	goals            []CharacterGoal
	evaluate         Evaluator
	result           Result
	cache            map[string]*Plan
	hadIndeterminate bool
	autoReference    bool
}

func Optimize(ctx context.Context, request Request, evaluate Evaluator) (Result, error) {
	t, err := prepare(request, evaluate)
	if err != nil {
		return Result{}, err
	}
	request = t.request
	baseline := map[string][]int{}
	complete := true
	for _, character := range request.Characters {
		baseline[character.Character] = append([]int(nil), character.Current...)
		if len(character.Current) != 5 {
			complete = false
		}
	}
	if complete {
		t.result.Baseline = t.evaluatePlan(ctx, baseline, request.SearchSeeds, false)
	}
	var best *Plan
	if t.result.Baseline != nil && t.result.Baseline.Qualified {
		best = t.result.Baseline
	}
	pools, err := t.pools()
	if err != nil {
		t.result.Status = "proven_infeasible"
		t.result.Message = err.Error()
		return t.result, nil
	}
	state := map[string][]int{}
	for _, character := range request.Characters {
		state[character.Character] = make([]int, 5)
		for i := range state[character.Character] {
			state[character.Character][i] = -1
		}
	}
	used := map[int]bool{}
	nodes := 0
	stopped := false
	searchLimit := max(1, request.EvaluationBudget-2*len(t.scenarios))
	var walk func(int)
	walk = func(position int) {
		if stopped {
			return
		}
		nodes++
		if ctx.Err() != nil || t.result.Evaluations+len(t.scenarios) > searchLimit || nodes > request.EvaluationBudget*5000 {
			stopped = true
			return
		}
		if position == len(pools) {
			plan := t.evaluatePlan(ctx, state, request.SearchSeeds, false)
			if plan.Qualified && (best == nil || Better(request.Mode, plan.Rank, best.Rank)) {
				best = plan
			}
			return
		}
		pool := pools[position]
		for _, id := range pool.ids {
			if used[id] {
				continue
			}
			state[pool.character][pool.slot] = id
			used[id] = true
			walk(position + 1)
			delete(used, id)
			state[pool.character][pool.slot] = -1
			if stopped {
				return
			}
		}
	}
	if request.Exact {
		walk(0)
		t.result.Exhaustive = !stopped
	} else {
		best = t.searchNeighborhoods(ctx, pools, best, searchLimit)
	}
	if ctx.Err() != nil {
		t.result.Status, t.result.Message = "cancelled", ctx.Err().Error()
		return t.result, nil
	}
	if t.autoReference {
		best = t.freezeReferences()
	}
	t.result.ReferenceGoals = t.goals
	if best == nil {
		if t.hadIndeterminate {
			t.result.Status = "indeterminate"
			t.result.Message = "部分候选未能完成有效模拟，请查看具体原因；本次结果不能证明无解"
		} else if t.result.Exhaustive {
			t.result.Status = "proven_infeasible"
			t.result.Message = "complete enumeration found no qualified assignment"
		} else {
			t.result.Status = "budget_no_feasible"
			t.result.Message = "budget ended before a feasible assignment was found"
		}
		return t.result, nil
	}
	if ctx.Err() != nil {
		t.result.Status = "cancelled"
		t.result.Message = ctx.Err().Error()
		return t.result, nil
	}
	validated := t.evaluatePlan(ctx, best.Equipment, request.ValidationSeeds, true)
	var verifiedBaseline *Plan
	if t.result.Baseline != nil && t.result.Baseline.Qualified {
		if best.Rank.Key == t.result.Baseline.Rank.Key {
			// Same equipment, scenarios and independent seed batch. Reuse both
			// success and failure; never rerun an identical batch to seek a pass.
			verifiedBaseline = validated
		} else {
			verifiedBaseline = t.evaluatePlan(ctx, t.result.Baseline.Equipment, request.ValidationSeeds, true)
		}
		t.result.Baseline = verifiedBaseline
	}
	if !validated.Qualified {
		if verifiedBaseline != nil && verifiedBaseline.Qualified {
			validated = verifiedBaseline
		} else {
			t.result.Status = "validation_failed"
			t.result.Message = "independent validation did not produce a qualified recommendation"
			return t.result, nil
		}
	}
	if verifiedBaseline != nil && verifiedBaseline.Qualified && !Better(request.Mode, validated.Rank, verifiedBaseline.Rank) {
		validated = verifiedBaseline
		t.result.Status = "feasible_baseline"
	} else {
		t.result.Status = "feasible_recommendation"
		if verifiedBaseline != nil && verifiedBaseline.Qualified {
			t.result.Improvement = CompareImprovement(request.Mode, t.weights, validated, verifiedBaseline, len(request.ValidationSeeds))
			if t.result.Improvement.State != "confirmed" {
				t.result.Status = "feasible_uncertain"
			}
		}
	}
	t.result.Plan = validated
	t.result.Impacts = t.impacts(validated.Equipment)
	t.result.Message = "qualified only for the declared independent validation batch; no global-optimum claim outside exhaustive mode"
	return t.result, nil
}

func normalize(value string) string {
	return strings.NewReplacer("_", "", "-", "", " ", "").Replace(strings.ToLower(value))
}

func prepare(request Request, evaluate Evaluator) (*task, error) {
	if evaluate == nil {
		return nil, errors.New("evaluation boundary is required")
	}
	if request.SchemaVersion != "1" || len(request.Characters) == 0 || len(request.Characters) > 16 || len(request.Items) > 10000 || len(request.Scenarios) == 0 || len(request.Scenarios) > 64 {
		return nil, errors.New("invalid optimization task size/schema")
	}
	if request.Mode == "" {
		request.Mode = "balanced"
	}
	if request.EvaluationBudget == 0 {
		request.EvaluationBudget = 256
	}
	if request.EvaluationBudget < 4*len(request.Scenarios) || request.EvaluationBudget > 100000 {
		return nil, errors.New("evaluation budget is outside supported bounds")
	}
	if len(request.SearchSeeds) == 0 || len(request.ValidationSeeds) == 0 || len(request.SearchSeeds) > engine.MaxEvaluationSamples || len(request.ValidationSeeds) > engine.MaxEvaluationSamples {
		return nil, errors.New("search and independent validation seeds are required")
	}
	seeds := map[int64]bool{}
	for _, seed := range request.SearchSeeds {
		if seeds[seed] {
			return nil, errors.New("duplicate search seed")
		}
		seeds[seed] = true
	}
	for _, seed := range request.ValidationSeeds {
		if seeds[seed] {
			return nil, errors.New("final seeds must be distinct from search seeds and each other")
		}
		seeds[seed] = true
	}
	t := &task{request: request, items: map[int]Item{}, reserved: map[int]string{}, weights: map[string]float64{}, evaluate: evaluate, cache: map[string]*Plan{}, result: Result{ScenarioAliases: map[string]string{}}}
	protected := map[string]bool{}
	for _, name := range request.ProtectedCharacters {
		protected[normalize(name)] = true
	}
	request.Characters = append([]Character(nil), request.Characters...)
	for i := range request.Characters {
		if protected[normalize(request.Characters[i].Character)] {
			request.Characters[i].Protected = true
		}
	}
	t.request = request
	characters := map[string]bool{}
	for _, character := range request.Characters {
		if character.Character == "" || characters[character.Character] {
			return nil, errors.New("selected characters must be unique")
		}
		characters[character.Character] = true
		if character.Protected {
			protected[normalize(character.Character)] = true
			if len(character.Current) != 5 {
				return nil, errors.New("protected character requires a complete current outfit")
			}
		}
		goal := character.CharacterGoal
		goal.Targets = append([]Target(nil), goal.Targets...)
		t.goals = append(t.goals, goal)
	}
	for _, item := range request.Items {
		if _, exists := t.items[item.ScanIndex]; exists || item.ScanIndex < 0 {
			return nil, errors.New("duplicate inventory identity")
		}
		t.items[item.ScanIndex] = item
		if item.Location != "" && protected[normalize(item.Location)] {
			t.reserved[item.ScanIndex] = normalize(item.Location)
		}
	}
	// Scan order is not slot order. Neighborhood swaps always operate on the
	// canonical five-slot order, while physical identities remain unchanged.
	for i := range t.request.Characters {
		c := &t.request.Characters[i]
		if len(c.Current) != 5 {
			continue
		}
		ordered := []int{-1, -1, -1, -1, -1}
		for _, id := range c.Current {
			for index, slot := range slots {
				if t.items[id].SlotKey == slot {
					ordered[index] = id
				}
			}
		}
		c.Current = ordered
	}
	seenScenario := map[string]string{}
	ids := map[string]string{}
	for _, scenario := range request.Scenarios {
		if scenario.ID == "" || !finite(scenario.Weight) || scenario.Weight < 0 {
			return nil, errors.New("invalid scenario identity/weight")
		}
		payload, _ := json.Marshal(struct {
			Evaluation   engine.Request
			Fixed        map[string][]int
			Participants []string
		}{scenario.Evaluation, scenario.FixedEquipment, scenario.Participants})
		hash := fmt.Sprintf("%x", sha256.Sum256(payload))
		if old, exists := ids[scenario.ID]; exists && old != hash {
			return nil, errors.New("one scenario id refers to conflicting definitions")
		}
		ids[scenario.ID] = hash
		if alias, exists := seenScenario[hash]; exists {
			if t.weights[alias] != scenario.Weight {
				return nil, errors.New("duplicate scenario has conflicting weights")
			}
			t.result.ScenarioAliases[scenario.ID] = alias
			continue
		}
		seenScenario[hash] = scenario.ID
		t.result.ScenarioAliases[scenario.ID] = scenario.ID
		t.weights[scenario.ID] = scenario.Weight
		t.scenarios = append(t.scenarios, scenario)
		for owner, items := range scenario.FixedEquipment {
			if characters[owner] {
				return nil, errors.New("a selected character cannot also be a fixed teammate")
			}
			for _, id := range items {
				item, ok := t.items[id]
				if !ok || normalize(item.Location) != normalize(owner) {
					return nil, errors.New("fixed teammate equipment is missing or belongs to another character")
				}
				if previous, ok := t.reserved[id]; ok && previous != normalize(owner) {
					return nil, errors.New("conflicting fixed inventory dependency")
				}
				t.reserved[id] = normalize(owner)
			}
		}
	}
	for i := range t.goals {
		unique := map[string]Target{}
		targets := []Target{}
		for j := range t.goals[i].Targets {
			target := &t.goals[i].Targets[j]
			alias, ok := t.result.ScenarioAliases[target.Scenario]
			if !ok {
				return nil, errors.New("goal refers to an unselected scenario")
			}
			target.Scenario = alias
			key := alias + "/" + target.Metric
			if previous, ok := unique[key]; ok {
				if previous.Weight != target.Weight || previous.Reference != target.Reference {
					return nil, errors.New("duplicate target has conflicting weight/reference")
				}
				continue
			}
			unique[key] = *target
			targets = append(targets, *target)
			if request.Mode != "balanced" && target.Reference == 0 && target.Weight > 0 && t.goals[i].Weight > 0 {
				t.autoReference = true
			}
		}
		t.goals[i].Targets = targets
	}
	if err := t.validateInputs(); err != nil {
		return nil, err
	}
	if _, err := Score("balanced", t.weights, zeroScores(t.weights), nil); err != nil {
		return nil, err
	}
	return t, nil
}

func zeroScores(weights map[string]float64) map[string]ScenarioScore {
	result := map[string]ScenarioScore{}
	for key := range weights {
		result[key] = ScenarioScore{}
	}
	return result
}

type pool struct {
	character string
	slot      int
	ids       []int
}

func (t *task) pools() ([]pool, error) {
	result := []pool{}
	for _, character := range t.request.Characters {
		for index, slot := range slots {
			p := pool{character: character.Character, slot: index}
			for id, item := range t.items {
				if item.SlotKey != slot {
					continue
				}
				if owner, ok := t.reserved[id]; ok && owner != normalize(character.Character) {
					continue
				}
				if fixed, ok := character.FixedSlots[slot]; ok && fixed != id {
					continue
				}
				if character.Protected && !contains(character.Current, id) {
					continue
				}
				if allowed := character.MainStats[slot]; len(allowed) > 0 && !containsString(allowed, item.MainStatKey) {
					continue
				}
				p.ids = append(p.ids, id)
			}
			if len(p.ids) == 0 {
				return nil, fmt.Errorf("no eligible %s for %s", slot, character.Character)
			}
			sort.Slice(p.ids, func(i, j int) bool {
				left, right := t.proxy(t.items[p.ids[i]], character), t.proxy(t.items[p.ids[j]], character)
				if left != right {
					return left > right
				}
				return p.ids[i] < p.ids[j]
			})
			result = append(result, p)
		}
	}
	sort.SliceStable(result, func(i, j int) bool { return len(result[i].ids) < len(result[j].ids) })
	return result, nil
}

func contains(values []int, value int) bool {
	for _, candidate := range values {
		if candidate == value {
			return true
		}
	}
	return false
}
func containsString(values []string, value string) bool {
	for _, candidate := range values {
		if candidate == value {
			return true
		}
	}
	return false
}
func (t *task) proxy(item Item, character Character) float64 {
	value := 0.0
	for _, stat := range item.Substats {
		weight := character.ProxyWeights[stat.Key]
		if len(character.ProxyWeights) == 0 {
			weight = 1
		}
		value += stat.Value * weight
	}
	if item.MainStatValue != nil {
		value += *item.MainStatValue * character.ProxyWeights[item.MainStatKey]
	}
	return value
}

func (t *task) recordIssue(message string) {
	if len(t.result.Issues) >= 16 {
		return
	}
	runes := []rune(message)
	if len(runes) > 1200 {
		message = string(runes[:1200]) + "…"
	}
	for _, old := range t.result.Issues {
		if old == message {
			return
		}
	}
	t.result.Issues = append(t.result.Issues, message)
}

func (t *task) evaluatePlan(ctx context.Context, state map[string][]int, seeds []int64, final bool) *Plan {
	encoded, _ := json.Marshal(state)
	key := string(encoded)
	if !final {
		if cached, ok := t.cache[key]; ok {
			return cached
		}
	}
	plan := &Plan{Equipment: map[string][]int{}, Reports: map[string]engine.Report{}, Qualified: true}
	for character, ids := range state {
		plan.Equipment[character] = append([]int(nil), ids...)
	}
	used := map[int]bool{}
	for _, character := range t.request.Characters {
		ids := state[character.Character]
		seenSlots := map[string]bool{}
		sets := map[string]int{}
		if len(ids) != 5 {
			plan.Qualified = false
			plan.Issues = append(plan.Issues, "incomplete outfit")
			continue
		}
		for _, id := range ids {
			item, ok := t.items[id]
			if !ok || used[id] || seenSlots[item.SlotKey] {
				plan.Qualified = false
				continue
			}
			used[id] = true
			seenSlots[item.SlotKey] = true
			sets[normalize(item.SetKey)]++
			if owner, ok := t.reserved[id]; ok && owner != normalize(character.Character) {
				plan.Qualified = false
			}
			if fixed, ok := character.FixedSlots[item.SlotKey]; ok && fixed != id {
				plan.Qualified = false
			}
			if allowed := character.MainStats[item.SlotKey]; len(allowed) > 0 && !containsString(allowed, item.MainStatKey) {
				plan.Qualified = false
			}
			if character.Protected && !contains(character.Current, id) {
				plan.Qualified = false
			}
			if !contains(character.Current, id) {
				plan.Rank.Changes++
			}
		}
		for set, count := range character.RequiredSets {
			if sets[normalize(set)] < count {
				plan.Qualified = false
			}
		}
	}
	if !plan.Qualified {
		plan.Issues = append(plan.Issues, "inventory constraints failed")
		return plan
	}
	scores := map[string]ScenarioScore{}
	for _, scenario := range t.scenarios {
		if ctx.Err() != nil {
			plan.Qualified = false
			plan.Issues = append(plan.Issues, ctx.Err().Error())
			break
		}
		r := scenario.Evaluation
		r.CompactSamples = true
		r.SchemaVersion = "1"
		r.EngineRevision = engine.Revision
		r.Seeds = append([]int64(nil), seeds...)
		r.Inventory = &t.request.Inventory
		r.Equipment = map[string][]engine.Artifact{}
		for owner, ids := range scenario.FixedEquipment {
			for _, id := range ids {
				r.Equipment[owner] = append(r.Equipment[owner], t.items[id].Artifact)
			}
		}
		for owner, ids := range state {
			if len(scenario.Participants) > 0 && !containsString(scenario.Participants, owner) {
				continue
			}
			for _, id := range ids {
				r.Equipment[owner] = append(r.Equipment[owner], t.items[id].Artifact)
			}
		}
		report, err := t.evaluate(ctx, r)
		t.result.Evaluations++
		if err != nil {
			plan.Qualified = false
			plan.Indeterminate = true
			t.hadIndeterminate = true
			plan.Issues = append(plan.Issues, scenario.ID+": "+err.Error())
			t.recordIssue(scenario.ID + ": " + err.Error())
			continue
		}
		if report.Validation.State != "passed" || !report.Validation.Complete {
			plan.Qualified = false
			if report.Validation.State != "failed" {
				plan.Indeterminate = true
				t.hadIndeterminate = true
			}
			plan.Issues = append(plan.Issues, scenario.ID+": validation "+report.Validation.State)
		}
		metrics := map[string]map[string]float64{}
		for _, character := range t.request.Characters {
			if len(scenario.Participants) > 0 && !containsString(scenario.Participants, character.Character) {
				continue
			}
			for stat, threshold := range character.MinimumStats {
				value, ok := report.InitialStats[character.Character][strings.ToLower(stat)]
				if !ok || !finite(value) {
					plan.Qualified, plan.Indeterminate, t.hadIndeterminate = false, true, true
					plan.Issues = append(plan.Issues, scenario.ID+": missing initial stat "+character.Character+"/"+stat)
				} else if value < threshold {
					plan.Qualified = false
					plan.Issues = append(plan.Issues, scenario.ID+": initial stat below minimum "+character.Character+"/"+stat)
				}
			}
		}
		for _, metric := range report.Metrics {
			if metrics[metric.Character] == nil {
				metrics[metric.Character] = map[string]float64{}
			}
			metrics[metric.Character][metric.Kind] = metric.Value
		}
		scores[scenario.ID] = ScenarioScore{DPS: report.MeanDPS, Metrics: metrics}
		if !final {
			report.Samples = nil
			report.BuffActivations = nil
			report.EnergyWindows = nil
		}
		plan.Reports[scenario.ID] = report
	}
	if plan.Qualified {
		mode, goals := t.request.Mode, t.goals
		if t.autoReference {
			mode, goals = "balanced", nil
		}
		rank, err := Score(mode, t.weights, scores, goals)
		if err != nil {
			plan.Qualified = false
			plan.Indeterminate = true
			t.hadIndeterminate = true
			plan.Issues = append(plan.Issues, err.Error())
		} else {
			rank.Changes = plan.Rank.Changes
			rank.Key = key
			plan.Rank = rank
		}
	}
	if !final {
		t.cache[key] = plan
	}
	return plan
}

func (t *task) impacts(state map[string][]int) []Impact {
	before := map[string][]int{}
	after := map[string][]int{}
	for _, item := range t.items {
		if item.Location != "" {
			owner := normalize(item.Location)
			before[owner] = append(before[owner], item.ScanIndex)
			after[owner] = append(after[owner], item.ScanIndex)
		}
	}
	touched := map[string]bool{}
	for owner, ids := range state {
		owner = normalize(owner)
		touched[owner] = true
		after[owner] = append([]int(nil), ids...)
		for _, id := range ids {
			donor := normalize(t.items[id].Location)
			if donor != "" && donor != owner {
				touched[donor] = true
				if _, selected := state[donor]; !selected {
					kept := []int{}
					for _, old := range after[donor] {
						if old != id {
							kept = append(kept, old)
						}
					}
					after[donor] = kept
				}
			}
		}
	}
	owners := []string{}
	for owner := range touched {
		owners = append(owners, owner)
	}
	sort.Strings(owners)
	result := []Impact{}
	for _, owner := range owners {
		sort.Ints(before[owner])
		sort.Ints(after[owner])
		result = append(result, Impact{Character: owner, Before: before[owner], After: after[owner]})
	}
	return result
}

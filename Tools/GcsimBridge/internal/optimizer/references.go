package optimizer

import "strconv"

// Reference search reuses the task's bounded discovery batch. Only qualified
// observations contribute; maxima for different goals need not be jointly wearable.
// Freeze once before final ranking and never update from validation seeds.
func (t *task) freezeReferences() *Plan {
	for i := range t.goals {
		for j := range t.goals[i].Targets {
			target := &t.goals[i].Targets[j]
			if target.Reference != 0 {
				continue
			}
			for _, plan := range t.cache {
				if !plan.Qualified {
					continue
				}
				for _, m := range plan.Reports[target.Scenario].Metrics {
					if m.Character == t.goals[i].Character && m.Kind == target.Metric && finite(m.Value) {
						target.Reference = max(target.Reference, m.Value)
					}
				}
			}
		}
	}
	t.autoReference = false
	t.result.ReferenceSource = "qualified_task_search_batch; budget=" + strconv.Itoa(t.result.Evaluations) + "; not_global_upper_bound"
	var best *Plan
	for _, plan := range t.cache {
		if !plan.Qualified {
			continue
		}
		scores := map[string]ScenarioScore{}
		for id, report := range plan.Reports {
			metrics := map[string]map[string]float64{}
			for _, m := range report.Metrics {
				if metrics[m.Character] == nil {
					metrics[m.Character] = map[string]float64{}
				}
				metrics[m.Character][m.Kind] = m.Value
			}
			scores[id] = ScenarioScore{DPS: report.MeanDPS, Metrics: metrics}
		}
		rank, err := Score(t.request.Mode, t.weights, scores, t.goals)
		if err != nil {
			plan.Qualified, plan.Indeterminate, t.hadIndeterminate = false, true, true
			plan.Issues = append(plan.Issues, err.Error())
			continue
		}
		rank.Changes, rank.Key = plan.Rank.Changes, plan.Rank.Key
		plan.Rank = rank
		if best == nil || Better(t.request.Mode, rank, best.Rank) {
			best = plan
		}
	}
	return best
}

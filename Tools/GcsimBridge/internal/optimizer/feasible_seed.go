package optimizer

import "context"

// Construct a feasible set/slot/occupancy seed before numerical optimization.
// A finite node budget is only a search limit, never an infeasibility proof.
func (t *task) feasibleSeed(ctx context.Context, pools []pool) map[string][]int {
	state := map[string][]int{}
	for _, c := range t.request.Characters {
		state[c.Character] = []int{-1, -1, -1, -1, -1}
	}
	used := map[int]bool{}
	nodes := 0
	canComplete := func() bool {
		for _, c := range t.request.Characters {
			for set, need := range c.RequiredSets {
				key := normalize(set)
				count := 0
				for _, id := range state[c.Character] {
					if id >= 0 && normalize(t.items[id].SetKey) == key {
						count++
					}
				}
				for _, p := range pools {
					if p.character != c.Character || state[c.Character][p.slot] >= 0 {
						continue
					}
					for _, id := range p.ids {
						if !used[id] && normalize(t.items[id].SetKey) == key {
							count++
							break
						}
					}
				}
				if count < need {
					return false
				}
			}
		}
		return true
	}
	var visit func(int) bool
	visit = func(position int) bool {
		nodes++
		if ctx.Err() != nil || nodes > max(10000, t.request.EvaluationBudget*200) {
			return false
		}
		if position == len(pools) {
			return canComplete()
		}
		p := pools[position]
		for _, id := range p.ids {
			if used[id] {
				continue
			}
			state[p.character][p.slot] = id
			used[id] = true
			if canComplete() && visit(position+1) {
				return true
			}
			delete(used, id)
			state[p.character][p.slot] = -1
		}
		return false
	}
	if visit(0) {
		return state
	}
	return nil
}

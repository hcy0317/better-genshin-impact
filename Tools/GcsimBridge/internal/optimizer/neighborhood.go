package optimizer

import (
	"context"
	"math/rand"
)

func cloneState(state map[string][]int) map[string][]int {
	copy := map[string][]int{}
	for owner, ids := range state {
		copy[owner] = append([]int(nil), ids...)
	}
	return copy
}

// All pools remain available. Local substitutions include reciprocal same-slot
// exchanges; deterministic random restarts explore set changes and deep pool items.
// Exhaustion of this bounded heuristic is never a proof of infeasibility.
func (t *task) searchNeighborhoods(ctx context.Context, pools []pool, best *Plan, limit int) *Plan {
	rng := rand.New(rand.NewSource(t.request.SearchSeeds[0]))
	consider := func(state map[string][]int) {
		if ctx.Err() != nil || t.result.Evaluations+len(t.scenarios) > limit {
			return
		}
		p := t.evaluatePlan(ctx, state, t.request.SearchSeeds, false)
		if p.Qualified && (best == nil || Better(t.request.Mode, p.Rank, best.Rank)) {
			best = p
		}
	}
	// Try the entire reverse-priority outfit as well as the forward one. This
	// resolves the common all-five-pieces contested by the same two characters case.
	for attempt := 0; attempt < 2; attempt++ {
		state, used := map[string][]int{}, map[int]bool{}
		for _, c := range t.request.Characters {
			state[c.Character] = []int{-1, -1, -1, -1, -1}
		}
		for _, p := range pools {
			for k := range p.ids {
				i := k
				if attempt == 1 {
					i = len(p.ids) - 1 - k
				}
				id := p.ids[i]
				if !used[id] {
					used[id] = true
					state[p.character][p.slot] = id
					break
				}
			}
		}
		consider(state)
	}
	for pass := 0; pass < max(8, t.request.EvaluationBudget*8); pass++ {
		if ctx.Err() != nil || t.result.Evaluations+len(t.scenarios) > limit {
			break
		}
		if best != nil {
			// Walk the full pool depth, not a permanently truncated Top-K list.
			for offset := range pools {
				p := pools[(pass+offset)%len(pools)]
				id := p.ids[(pass/2)%len(p.ids)]
				state := cloneState(best.Equipment)
				old := state[p.character][p.slot]
				for owner, ids := range state {
					if owner != p.character && ids[p.slot] == id {
						ids[p.slot] = old
					}
				}
				state[p.character][p.slot] = id
				consider(state)
			}
		}
		state, used := map[string][]int{}, map[int]bool{}
		for _, c := range t.request.Characters {
			state[c.Character] = []int{-1, -1, -1, -1, -1}
		}
		// Rotate priority between roles and expand over every real instance.
		for _, index := range rng.Perm(len(pools)) {
			p := pools[index]
			start := rng.Intn(len(p.ids))
			for n := range p.ids {
				id := p.ids[(start+n)%len(p.ids)]
				if !used[id] {
					used[id] = true
					state[p.character][p.slot] = id
					break
				}
			}
		}
		consider(state)
	}
	return best
}

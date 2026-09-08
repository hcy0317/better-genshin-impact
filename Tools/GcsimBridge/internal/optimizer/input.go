package optimizer

import (
	"errors"
	"sort"
	"strings"
)

func (t *task) validateInputs() error {
	if t.request.Mode != "balanced" && t.request.Mode != "peak" && t.request.Mode != "fallback" {
		return errors.New("unknown mode")
	}
	selected := map[string]bool{}
	for _, c := range t.request.Characters {
		if c.Character != normalize(c.Character) || selected[c.Character] || !finite(c.Weight) || c.Weight < 0 {
			return errors.New("canonical unique character keys and finite weights are required")
		}
		selected[c.Character] = true
		sets := 0
		for key, n := range c.RequiredSets {
			if key == "" || n < 1 || n > 5 {
				return errors.New("invalid required set count")
			}
			sets += n
		}
		if sets > 5 {
			return errors.New("required sets exceed five slots")
		}
		for slot, id := range c.FixedSlots {
			if !containsString(slots, slot) || t.items[id].SlotKey != slot {
				return errors.New("fixed piece does not exist in the declared slot")
			}
		}
		for slot := range c.MainStats {
			if !containsString(slots, slot) {
				return errors.New("unknown main-stat slot")
			}
		}
		for key, v := range c.MinimumStats {
			if key == "" || !finite(v) || v < 0 {
				return errors.New("invalid minimum stat")
			}
		}
		for key, v := range c.ProxyWeights {
			if key == "" || !finite(v) || v < 0 {
				return errors.New("invalid search proxy")
			}
		}
		current := map[int]bool{}
		for _, id := range c.Current {
			item, ok := t.items[id]
			if !ok || current[id] || (item.Location != "" && normalize(item.Location) != c.Character) {
				return errors.New("current outfit has invalid inventory ownership")
			}
			if c.Protected && normalize(item.Location) != c.Character {
				return errors.New("protected outfit requires observed ownership")
			}
			current[id] = true
		}
	}
	for _, item := range t.items {
		if !containsString(slots, item.SlotKey) {
			return errors.New("unknown inventory slot")
		}
	}
	appears := map[string]bool{}
	for _, s := range t.scenarios {
		participants := map[string]bool{}
		for _, name := range s.Participants {
			if !selected[name] || participants[name] {
				return errors.New("scenario participants must name selected characters once")
			}
			participants[name], appears[name] = true, true
		}
		if len(s.Participants) == 0 {
			for name := range selected {
				appears[name] = true
			}
		}
		for owner, ids := range s.FixedEquipment {
			if owner != normalize(owner) || len(ids) != 5 {
				return errors.New("fixed real teammate requires five pieces and canonical key")
			}
			seen := map[string]bool{}
			for _, id := range ids {
				slot := t.items[id].SlotKey
				if seen[slot] {
					return errors.New("fixed teammate has duplicate slot")
				}
				seen[slot] = true
			}
		}
	}
	for name := range selected {
		if !appears[name] {
			return errors.New("every selected character must appear in an evaluated scenario")
		}
	}
	for i := range t.goals {
		goal := &t.goals[i]
		positive := false
		for _, target := range goal.Targets {
			if target.Metric == "" || !finite(target.Weight) || target.Weight < 0 || !finite(target.Reference) || target.Reference < 0 {
				return errors.New("invalid role target")
			}
			positive = positive || target.Weight > 0
			for _, s := range t.scenarios {
				if s.ID == target.Scenario && len(s.Participants) > 0 && !containsString(s.Participants, goal.Character) {
					return errors.New("role target is absent from its scenario")
				}
			}
		}
		if t.request.Mode != "balanced" && goal.Weight > 0 && !positive {
			return errors.New("positive role weight requires a positive Build target")
		}
		sort.Slice(goal.Targets, func(i, j int) bool {
			a, b := goal.Targets[i], goal.Targets[j]
			return strings.Compare(a.Scenario+"/"+a.Metric, b.Scenario+"/"+b.Metric) < 0
		})
	}
	sort.Slice(t.goals, func(i, j int) bool { return t.goals[i].Character < t.goals[j].Character })
	return nil
}

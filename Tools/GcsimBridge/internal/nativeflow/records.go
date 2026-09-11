package nativeflow

import "github.com/genshinsim/gcsim/pkg/core/info"

// Bind source records to the pinned SDK's live statuses, including delayed
// activation, hitlag, stance loss and Kokomi's native refresh. No durations or
// damage multipliers are manufactured by the adapter.
func (e *Evaluator) nativeRemaining(n Node) (int, bool) {
	c := e.char(n.Character)
	if c == nil {
		return 0, false
	}
	charStatus, globalStatus := "", ""
	switch n.Character + "/" + n.Kind {
	case "furina/skill":
		charStatus = "furina-skill"
	case "fischl/skill":
		charStatus = "oz-active"
	case "raidenshogun/skill":
		charStatus = "raiden-e"
	case "raidenshogun/burst":
		charStatus = "raidenburst"
	case "navia/skill":
		charStatus = "navia-a1-dmg"
	case "kukishinobu/skill":
		globalStatus = "kuki-e"
	case "sangonomiyakokomi/skill":
		globalStatus = "kokomiskill"
	case "sangonomiyakokomi/burst":
		globalStatus = "kokomiburst"
	case "bennett/burst":
		globalStatus = "bennettburst"
	case "nahida/skill":
		remaining := 0
		for _, raw := range e.core.Combat.Enemies() {
			if target, ok := raw.(info.Enemy); ok && target.StatusIsActive("nahida-e") {
				remaining = max(remaining, target.StatusExpiry("nahida-e")-e.core.F)
			}
		}
		return remaining, true
	}
	if charStatus != "" {
		return c.StatusDuration(charStatus), true
	}
	if globalStatus != "" {
		return e.core.Status.Duration(globalStatus), true
	}
	return 0, false
}

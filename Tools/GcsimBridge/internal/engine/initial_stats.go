package engine

import (
	"github.com/genshinsim/gcsim/pkg/core"
	"strings"
)

// Frame-zero panel values in GOOD units, before manual buffs or rotation actions.
// These are not promises of in-combat buff uptime.
func initialStats(c *core.Core) map[string]map[string]float64 {
	result := map[string]map[string]float64{}
	for _, character := range c.Player.Chars() {
		values := map[string]float64{}
		for key, stat := range inventoryStats {
			value := character.Stat(stat)
			if strings.HasSuffix(key, "_") {
				value *= 100
			}
			values[key] = value
		}
		values["hp"] = character.MaxHP()
		values["atk"] = character.TotalAtk()
		values["def"] = character.TotalDef(false)
		result[character.Base.Key.String()] = values
	}
	return result
}

package engine

import (
	"errors"
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/genshinsim/gcsim/pkg/core/attributes"
	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/core/keys"
)

// GOOD/BetterGI inventory units: a trailing underscore means percentage points.
// Character, weapon and set mechanics still come entirely from gcsim.
var inventoryStats = map[string]attributes.Stat{
	"hp": attributes.HP, "hp_": attributes.HPP, "atk": attributes.ATK, "atk_": attributes.ATKP,
	"def": attributes.DEF, "def_": attributes.DEFP, "elemas": attributes.EM,
	"enerrech_": attributes.ER, "critrate_": attributes.CR, "critdmg_": attributes.CD,
	"heal_": attributes.Heal, "physical_dmg_": attributes.PhyP,
	"pyro_dmg_": attributes.PyroP, "hydro_dmg_": attributes.HydroP,
	"cryo_dmg_": attributes.CryoP, "electro_dmg_": attributes.ElectroP,
	"anemo_dmg_": attributes.AnemoP, "geo_dmg_": attributes.GeoP, "dendro_dmg_": attributes.DendroP,
}

func inventoryStat(key string, value float64) (attributes.Stat, float64, error) {
	key = strings.ToLower(key)
	stat, ok := inventoryStats[key]
	if !ok || math.IsNaN(value) || math.IsInf(value, 0) || value < 0 {
		return 0, 0, fmt.Errorf("unsupported or invalid inventory stat %q", key)
	}
	if strings.HasSuffix(key, "_") {
		value /= 100
	}
	return stat, value, nil
}

func validMainStat(slot string, stat attributes.Stat) bool {
	base := stat == attributes.HPP || stat == attributes.ATKP || stat == attributes.DEFP || stat == attributes.EM
	switch slot {
	case "flower":
		return stat == attributes.HP
	case "plume":
		return stat == attributes.ATK
	case "sands":
		return base || stat == attributes.ER
	case "goblet":
		return base || (stat >= attributes.PyroP && stat <= attributes.PhyP)
	case "circlet":
		return base || stat == attributes.CR || stat == attributes.CD || stat == attributes.Heal
	default:
		return false
	}
}

func applyEquipment(cfg *info.ActionList, request Request) error {
	if len(request.Equipment) == 0 {
		return nil
	}
	ref := request.Inventory
	if ref == nil || ref.UID == "" || ref.ScanSessionID == "" || ref.CatalogVersion == "" || ref.SnapshotDigest == "" {
		return errors.New("inventory equipment requires the complete existing snapshot identity")
	}
	used := make(map[int]bool)
	applied := 0
	for index := range cfg.Characters {
		profile := &cfg.Characters[index]
		pieces, ok := request.Equipment[profile.Base.Key.String()]
		if !ok {
			continue
		}
		applied++
		if len(pieces) != 5 {
			return errors.New("each inventory character requires exactly five artifacts")
		}
		if len(profile.Sets) != 0 || profile.RandomSubstats != nil {
			return errors.New("remove configured sets/random stats before applying inventory equipment")
		}
		for _, value := range profile.Stats {
			if value != 0 {
				return errors.New("remove configured artifact stats before applying inventory equipment; base/weapon stats are provided by gcsim")
			}
		}
		pieces = append([]Artifact(nil), pieces...)
		sort.Slice(pieces, func(i, j int) bool { return pieces[i].SlotKey < pieces[j].SlotKey })
		stats := make([]float64, attributes.EndStatType)
		sets := make(info.Sets)
		supplemental := map[string]int{}
		slots := make(map[string]bool)
		for _, piece := range pieces {
			if piece.ScanIndex < 0 || used[piece.ScanIndex] || slots[piece.SlotKey] {
				return errors.New("duplicate artifact instance or slot in the shared snapshot")
			}
			if piece.MainStatValue == nil || *piece.MainStatValue <= 0 {
				return errors.New("main-stat value must be resolved by the versioned source catalog")
			}
			main, value, err := inventoryStat(piece.MainStatKey, *piece.MainStatValue)
			if err != nil {
				return err
			}
			if !validMainStat(piece.SlotKey, main) {
				return errors.New("main stat is invalid for the artifact slot")
			}
			if !isSupplementalSet(piece.SetKey) {
				set, err := keys.SetString(strings.ToLower(piece.SetKey))
				if err != nil {
					return fmt.Errorf("unsupported inventory set: %w", err)
				}
				if set.String() == "" || set.String() == "invalidset" {
					return errors.New("missing or invalid inventory set")
				}
				sets[set]++
			} else {
				supplemental[strings.ToLower(piece.SetKey)]++
			}
			stats[main] += value
			seen := map[attributes.Stat]bool{main: true}
			if len(piece.Substats) > 4 {
				return errors.New("artifact has more than four substats")
			}
			for _, sub := range piece.Substats {
				if sub.Dormant {
					return errors.New("dormant substats require a supported activation model")
				}
				stat, value, err := inventoryStat(sub.Key, sub.Value)
				if err != nil {
					return err
				}
				if seen[stat] || stat >= attributes.Heal {
					return errors.New("duplicate or unsupported artifact substat")
				}
				seen[stat] = true
				stats[stat] += value
			}
			used[piece.ScanIndex], slots[piece.SlotKey] = true, true
		}
		// Permanent bonuses must be present before SDK character initialization.
		for _, count := range supplemental {
			if count >= 2 {
				stats[attributes.ATKP] += .18
			}
		}
		profile.Stats = stats
		profile.StatsByLabel = map[string][]float64{"inventory": append([]float64(nil), stats...)}
		profile.Sets = sets
	}
	if applied != len(request.Equipment) {
		return errors.New("equipment references a character absent from this scenario")
	}
	return nil
}

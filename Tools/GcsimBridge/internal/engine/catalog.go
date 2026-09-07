package engine

import (
	"github.com/genshinsim/gcsim/pkg/catalog"
	"github.com/genshinsim/gcsim/pkg/model"
	"sort"
)

// Export upstream data with the very same revision as the executable mechanics.
// Numeric game IDs permit Enka mapping without a separately hand-maintained roster.
func Catalog() map[string]any {
	characters := make([]*model.AvatarData, 0, len(catalog.CharacterMap))
	weapons := make([]*model.WeaponData, 0, len(catalog.WeaponMap))
	sets := make([]*model.ArtifactData, 0, len(catalog.ArtifactMap))
	for _, value := range catalog.CharacterMap {
		characters = append(characters, value)
	}
	for _, value := range catalog.WeaponMap {
		weapons = append(weapons, value)
	}
	for _, value := range catalog.ArtifactMap {
		sets = append(sets, value)
	}
	sort.Slice(characters, func(i, j int) bool { return characters[i].Key < characters[j].Key })
	sort.Slice(weapons, func(i, j int) bool { return weapons[i].Key < weapons[j].Key })
	sort.Slice(sets, func(i, j int) bool { return sets[i].Key < sets[j].Key })
	return map[string]any{"engineRevision": Revision, "adapterVersion": AdapterVersion, "characters": characters, "weapons": weapons, "sets": sets, "capabilities": Capabilities()}
}

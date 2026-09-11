package engine

import (
	"encoding/json"
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
	if isSupplementalSet(heartOfTheFurnace) {
		sets = append(sets, &model.ArtifactData{SetId: 15048, Key: heartOfTheFurnace})
	}
	if isSupplementalSet(scarletProof) {
		sets = append(sets, &model.ArtifactData{SetId: 15047, Key: scarletProof})
	}
	var names map[string]map[string]string
	if err := json.Unmarshal(chineseNames, &names); err != nil {
		panic(err)
	}
	if names["artifact_names"] == nil {
		names["artifact_names"] = map[string]string{}
	}
	if SupplementalCharacterRevision != "" {
		if names["character_names"] == nil {
			names["character_names"] = map[string]string{}
		}
		names["character_names"]["sandrone"] = "桑多涅"
	}
	if isSupplementalSet(heartOfTheFurnace) {
		names["artifact_names"][heartOfTheFurnace] = "炉火融炼之心"
	}
	if isSupplementalSet(scarletProof) {
		names["artifact_names"][scarletProof] = "血红之证"
	}
	sort.Slice(characters, func(i, j int) bool { return characters[i].Key < characters[j].Key })
	sort.Slice(weapons, func(i, j int) bool { return weapons[i].Key < weapons[j].Key })
	sort.Slice(sets, func(i, j int) bool { return sets[i].Key < sets[j].Key })
	return map[string]any{"engineRevision": Revision, "adapterVersion": AdapterVersion, "characters": characters, "weapons": weapons, "sets": sets, "localization": names, "capabilities": Capabilities()}
}

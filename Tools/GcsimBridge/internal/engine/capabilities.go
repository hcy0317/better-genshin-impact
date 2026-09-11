package engine

import (
	"github.com/genshinsim/gcsim/pkg/core/keys"
	resultinfo "github.com/genshinsim/gcsim/pkg/result"
)

func Capabilities() map[string]any {
	incomplete := []string{}
	known := []string{}
	for _, key := range keys.CharValues() {
		if key == keys.InvalidChar || key.String() == "" {
			continue
		}
		known = append(known, key.String())
		if key != keys.InvalidChar && !resultinfo.IsCharacterComplete(key) {
			incomplete = append(incomplete, key.String())
		}
	}
	sets := []string{}
	for _, key := range keys.SetStrings() {
		if key != "" && key != "invalidset" {
			sets = append(sets, key)
		}
	}
	supplemental := []string{}
	for _, key := range []string{heartOfTheFurnace, scarletProof} {
		if isSupplementalSet(key) {
			sets = append(sets, key)
			supplemental = append(supplemental, key)
		}
	}
	return map[string]any{
		"roundCountTermination": true, "maxRoundCount": 64,
		"nativeFlow":    map[string]any{"schemaVersion": "native-flow-v1", "features": []string{"nine-strategies-v1", "structure-v1"}, "macros": []string{"neuvillette_charge_v1"}, "execution": "simulation_reference_only", "maxNodes": 600},
		"schemaVersion": "1", "engineRevision": Revision, "adapterVersion": AdapterVersion,
		"buffKinds": []string{"stat", "resistance", "defense_reduction", "attack_bonus"},
		"anchors":   []string{"start", "round", "action"}, "actionAnchors": []string{"attack", "skill", "burst"},
		"attackTags": []string{"normal", "skill", "burst"}, "stacking": "refresh_same_id",
		"framesPerSecond": 60, "scoringWindows": "[startFrame,endFrame)",
		"roundAnchor":          "actual_main_loop_boundaries_or_legacy_declared_windows",
		"duration":             "actual_round_count_or_legacy_low_level_stop_modes",
		"automaticRoundTiming": true, "variableDurationDps": true, "maxSamples": MaxEvaluationSamples,
		"maxTrajectorySeconds": MaxTrajectorySeconds,
		"coverageRatio":        "explicit_approximation_not_damage_share",
		"nativeReplacement":    false, "automaticNativeOverlapDetection": false,
		"extraBaseDamage": false, "generalReactionBonus": false, "shieldConstraint": false,
		"trajectoryConstraints": []string{"min_actions", "max_failed_wait_frames", "min_effective_healing"},
		"knownCharacterKeys":    known, "upstreamMarkedIncomplete": incomplete,
		"knownArtifactSetKeys":   sets,
		"supplementalCharacters": supplementalCharacterCapabilities(),
		"supplementalSets":       map[string]any{"revision": SupplementalSetRevision, "source": "https://github.com/theBowja/genshin-db/tree/" + SupplementalSetRevision + "/src/data/English/artifacts", "keys": supplemental, "input": "inventory_equipment", "effects": "native_sdk_events"},
		"supportBoundary":        "key_presence_is_not_proof_that_every_configuration_is_complete_or_runnable",
	}
}

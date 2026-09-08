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
	return map[string]any{
		"schemaVersion": "1", "engineRevision": Revision, "adapterVersion": AdapterVersion,
		"buffKinds": []string{"stat", "resistance", "defense_reduction", "attack_bonus"},
		"anchors":   []string{"start", "round", "action"}, "actionAnchors": []string{"attack", "skill", "burst"},
		"attackTags": []string{"normal", "skill", "burst"}, "stacking": "refresh_same_id",
		"framesPerSecond": 60, "scoringWindows": "[startFrame,endFrame)",
		"roundAnchor":          "actual_main_loop_boundaries_or_legacy_declared_windows",
		"duration":             "fixed_simulation_frames_or_native_target_and_script_completion",
		"automaticRoundTiming": true, "variableDurationDps": true, "maxSamples": MaxEvaluationSamples,
		"maxTrajectorySeconds": MaxTrajectorySeconds,
		"coverageRatio":        "explicit_approximation_not_damage_share",
		"nativeReplacement":    false, "automaticNativeOverlapDetection": false,
		"extraBaseDamage": false, "generalReactionBonus": false, "shieldConstraint": false,
		"trajectoryConstraints": []string{"min_actions", "max_failed_wait_frames", "min_effective_healing"},
		"knownCharacterKeys":    known, "upstreamMarkedIncomplete": incomplete,
		"knownArtifactSetKeys": sets,
		"supportBoundary":      "key_presence_is_not_proof_that_every_configuration_is_complete_or_runnable",
	}
}

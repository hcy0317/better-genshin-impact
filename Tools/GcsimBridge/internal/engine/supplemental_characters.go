package engine

import (
	"github.com/genshinsim/gcsim/pkg/core/keys"
	resultinfo "github.com/genshinsim/gcsim/pkg/result"
)

// Set only by the isolated source-verified character build. Ordinary builds
// neither register nor claim these experimental character implementations.
var SupplementalCharacterRevision string

func characterComplete(key keys.Char) bool {
	return resultinfo.IsCharacterComplete(key) && !(key.String() == "sandrone" && SupplementalCharacterRevision != "")
}

func supplementalCharacterCapabilities() map[string]any {
	keys := []string{}
	if SupplementalCharacterRevision != "" {
		keys = append(keys, "sandrone")
	}
	return map[string]any{
		"modelRevision": SupplementalCharacterRevision, "keys": keys, "complete": false,
		"sourceRevision":    "8b15995fa220c88a4d0d7ffe1e21b041d0b32588",
		"status":            "experimental_incomplete_explicit_trial_only",
		"missingActions":    []string{"attack", "low_plunge", "high_plunge"},
		"timingAssumptions": []string{"E hitmarks20/24f and42f animation are provisional", "Q hitmarks36/48/60f are provisional; final gap24f is approximate source timing", "C6 extra hits coincide with original beams3-6 as a provisional timing model", "Sweep and unconverted C6 aura cadence remains unverified", "Overlapping Radiance uses Stellar Swirl priority provisionally"},
	}
}

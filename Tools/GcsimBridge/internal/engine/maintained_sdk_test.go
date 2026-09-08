package engine_test

import (
	"encoding/json"
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"math"
	"testing"
)

func TestManualResistanceElementsRemainNamedAndRejectNonDamageAuras(t *testing.T) {
	base := engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17}, Config: fixedConfig}
	reference, err := engine.Evaluate(base)
	if err != nil {
		t.Fatal(err)
	}
	for _, element := range []string{"pyro", "hydro", "cryo", "electro", "anemo", "geo", "dendro", "physical", "none", "unknown", "frozen", "quicken", ""} {
		t.Run(element, func(t *testing.T) {
			request := base
			request.Buffs = []engine.Buff{{ID: "element", Kind: "resistance", Target: "all_enemies", Element: element, Unit: "fraction", Value: -.1, DurationFrames: -1, Anchor: "start", Relationship: "additional"}}
			report, err := engine.Evaluate(request)
			valid := element != "none" && element != "unknown" && element != "frozen" && element != "quicken" && element != ""
			if !valid {
				if err == nil {
					t.Fatal("non-damage element was accepted")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			expected := 1.0
			if element == "physical" {
				expected = 1 / .9
			}
			if math.Abs(report.MeanDPS/reference.MeanDPS-expected) > 1e-10 {
				t.Fatalf("element ordinal affected a different damage type: %s", element)
			}
		})
	}
}

func TestMaintainedPruneDataAndMechanicsAreAvailableTogether(t *testing.T) {
	catalog, _ := json.Marshal(engine.Catalog())
	var data map[string]any
	_ = json.Unmarshal(catalog, &data)
	names := data["localization"].(map[string]any)["character_names"].(map[string]any)
	if names["prune"] != "布伦妮" {
		t.Fatal("maintained Chinese name is missing")
	}
	for _, cons := range []int{0, 6} {
		t.Run(fmt.Sprint(cons), func(t *testing.T) {
			config := fmt.Sprintf(`options duration=24;target lvl=100 resist=0.1 radius=2 pos=0,2.4;
prune char lvl=90/90 cons=%d talent=6,6,6;prune add weapon="apprenticesnotes" refine=1 lvl=90/90;
active prune;prune skill;prune burst;prune attack:3;wait(120);prune skill;prune attack:3;`, cons)
			report, err := engine.Evaluate(engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: config, Seeds: []int64{17, 29}})
			if err != nil {
				t.Fatal(err)
			}
			if report.MeanDPS <= 0 || report.Support != "native_simulation" {
				t.Fatalf("new character only exists as metadata: %+v", report)
			}
			skill, burst := false, false
			for _, event := range report.Samples[0].Characters[0].ActionEvents {
				skill = skill || event.Action == "skill"
				burst = burst || event.Action == "burst"
			}
			if !skill || !burst {
				t.Fatal("new character did not execute both real skill and burst")
			}
		})
	}
}

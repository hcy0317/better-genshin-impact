package engine

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestWeaponWaitDiagnosticsDoNotChangeNativeCompletion(t *testing.T) {
	for _, weapon := range []string{"huntersbow", "favoniuswarbow"} {
		config := "options duration=1;target lvl=90 resist=0.1;\namber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon=\"" + weapon + "\" refine=5 lvl=90/90;\nactive amber;for let i=0;i<1;i=i+1 {while !.amber.mods.favonius-cd {amber attack;}}"
		result, err := Evaluate(Request{SchemaVersion: "1", EngineRevision: Revision, Config: config, Seeds: []int64{17}, RotationLineOffset: 2})
		if err != nil || !result.Validation.Complete {
			t.Fatalf("valid completion rejected: %v", err)
		}
		data, _ := json.Marshal(result)
		var fields map[string]json.RawMessage
		_ = json.Unmarshal(data, &fields)
		if weapon == "huntersbow" && (!strings.Contains(string(fields["warnings"]), "安柏") || !strings.Contains(string(fields["warnings"]), "第1行")) {
			t.Fatalf("missing localized, line-addressable weapon dependency warning: %s", fields["warnings"])
		}
		if weapon == "favoniuswarbow" && len(fields["warnings"]) > 0 {
			t.Fatal("matching weapon was warned as missing")
		}
	}
}

func TestCommentsAndStringsAreNotWeaponDependencies(t *testing.T) {
	config := `options duration=1;target lvl=90 resist=0.1;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;
let note="while !.amber.mods.favonius-cd {amber attack;}";
# while !.amber.mods.favonius-cd {amber attack;}
active amber; amber attack;`
	result, err := Evaluate(Request{SchemaVersion: "1", EngineRevision: Revision, Config: config, Seeds: []int64{17}})
	if err != nil {
		t.Fatal(err)
	}
	data, _ := json.Marshal(result)
	var fields map[string]json.RawMessage
	_ = json.Unmarshal(data, &fields)
	if len(fields["warnings"]) > 0 {
		t.Fatal("text was mistaken for executable dependency")
	}
}

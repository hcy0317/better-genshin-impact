package nativeflow

import "testing"

func TestStructuralSearchChangesOrderAndCanDropOptionalOutputButNotRequiredProducer(t *testing.T) {
	p := &Program{SchemaVersion: "native-flow-v1", Source: "fixture", Root: []Node{
		{ID: "shield", Kind: "skill", Character: "zhongli", Options: map[string]string{"record": "shield", "required": "true"}},
		{ID: "optional-q", Kind: "burst", Character: "jean"},
		{ID: "hit", Kind: "attack", Character: "jean", Seconds: 1, Options: map[string]string{"keep": "shield", "required": "true"}},
	}}
	variants := p.StructuralCandidates(64)
	swapped, dropped := false, false
	for _, v := range variants {
		swapped = swapped || v.Edit.Kind == "swap"
		dropped = dropped || v.Edit.Kind == "drop" && v.Edit.Node == "optional-q"
		if v.Edit.Kind == "drop" && v.Edit.Node != "optional-q" {
			t.Fatal("protected output or producer removed")
		}
		if err := v.Program.Validate(); err != nil {
			t.Fatal(err)
		}
	}
	if !swapped || !dropped {
		t.Fatalf("missing structural candidates: swap=%v drop=%v", swapped, dropped)
	}
	if p.Root[0].ID != "shield" || len(p.Root) != 3 {
		t.Fatal("source program mutated")
	}
}

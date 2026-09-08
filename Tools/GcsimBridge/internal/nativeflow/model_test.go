package nativeflow_test

import (
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"testing"
)

func TestRawGuardCannotSilentlyLoseItsCompiledCondition(t *testing.T) {
	program := &nativeflow.Program{SchemaVersion: "native-flow-v1", Source: "琴 e(if=q-ready(琴))", Root: []nativeflow.Node{{ID: "e", Kind: "skill", Character: "jean", Options: map[string]string{"if": "q-ready(琴)"}}}}
	if program.Validate() == nil {
		t.Fatal("raw guard text was accepted without executable condition")
	}
}

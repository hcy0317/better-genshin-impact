package engine_test

import (
	"testing"

	"github.com/genshinsim/gcsim/pkg/gcs/ast"
	"github.com/genshinsim/gcsim/pkg/gcs/eval"
	"github.com/genshinsim/gcsim/pkg/gcs/parser"
	"github.com/genshinsim/gcsim/pkg/simulation"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

// A small, trusted test fixture; deliberately retains real energy rules.
const fixedConfig = `
options duration=20 workers=1 iteration=1;
target lvl=90 resist=0.1;
amber char lvl=90/90 cons=0 talent=6,6,6;
amber add weapon="favoniuswarbow" refine=1 lvl=90/90;
amber add stats hp=4780 atk=311 atk%=0.466 pyro%=0.466 cr=0.311;
active amber;
while 1 { amber attack:3; }
`

func TestFixedSeedMatchesUpstreamTrajectory(t *testing.T) {
	file := ast.NewFile()
	cfg, script, err := parser.New(file, fixedConfig).Parse()
	if err != nil {
		t.Fatal(err)
	}
	core, err := simulation.NewCore(17, false, cfg)
	if err != nil {
		t.Fatal(err)
	}
	evaluator, err := eval.NewEvaluator(file, script, core)
	if err != nil {
		t.Fatal(err)
	}
	sim, err := simulation.New(cfg, evaluator, core)
	if err != nil {
		t.Fatal(err)
	}
	reference, err := sim.Run()
	if err != nil {
		t.Fatal(err)
	}
	if reference.TotalDamage <= 0 {
		t.Fatal("reference fixture did not deal damage")
	}

	actual, err := engine.Evaluate(engine.Request{
		SchemaVersion: "1", EngineRevision: engine.Revision,
		Config: fixedConfig, Seeds: []int64{17},
	})
	if err != nil {
		t.Fatal(err)
	}
	if actual.EngineRevision != engine.Revision || len(actual.Samples) != 1 {
		t.Fatalf("missing engine/sample identity: %+v", actual)
	}
	sample := actual.Samples[0]
	if sample.Seed != reference.Seed || sample.Duration != reference.Duration ||
		sample.TotalDamage != reference.TotalDamage || actual.MeanDPS != reference.DPS {
		t.Fatalf("adapter changed upstream results: %+v, want %+v", sample, reference)
	}
	if len(sample.Characters) != 1 || len(sample.Characters[0].ActionEvents) == 0 {
		t.Fatal("raw successful actions are required, not just a DPS number")
	}
}

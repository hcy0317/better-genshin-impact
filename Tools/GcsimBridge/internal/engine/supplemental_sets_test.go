package engine_test

import (
	"github.com/genshinsim/gcsim/pkg/core"
	"github.com/genshinsim/gcsim/pkg/core/attacks"
	"github.com/genshinsim/gcsim/pkg/core/attributes"
	"github.com/genshinsim/gcsim/pkg/core/event"
	"github.com/genshinsim/gcsim/pkg/core/info"
	"github.com/genshinsim/gcsim/pkg/gcs/ast"
	"github.com/genshinsim/gcsim/pkg/gcs/eval"
	"github.com/genshinsim/gcsim/pkg/gcs/parser"
	"github.com/genshinsim/gcsim/pkg/simulation"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"math"
	"testing"
)

func supplementalCore(t *testing.T) *core.Core {
	t.Helper()
	file := ast.NewFile()
	cfg, script, err := parser.New(file, `options duration=30;target lvl=90 resist=0.1;amber char lvl=90/90 cons=0 talent=6,6,6;amber add weapon="huntersbow" refine=1 lvl=90/90;kaeya char lvl=90/90 cons=0 talent=6,6,6;kaeya add weapon="dullblade" refine=1 lvl=90/90;lisa char lvl=90/90 cons=0 talent=6,6,6;lisa add weapon="apprenticesnotes" refine=1 lvl=90/90;active amber;amber attack;`).Parse()
	if err != nil {
		t.Fatal(err)
	}
	c, err := simulation.NewCore(17, false, cfg)
	if err != nil {
		t.Fatal(err)
	}
	ev, err := eval.NewEvaluator(file, script, c)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = simulation.New(cfg, ev, c); err != nil {
		t.Fatal(err)
	}
	return c
}
func fourPieces(key string) []engine.Artifact {
	return []engine.Artifact{{SetKey: key}, {SetKey: key}, {SetKey: key}, {SetKey: key}}
}
func closeStat(t *testing.T, got, want float64) {
	t.Helper()
	if math.Abs(got-want) > 1e-9 {
		t.Fatalf("got %v want %v", got, want)
	}
}

func TestHeartOfFurnaceTriggersOffFieldRefreshesAndDoesNotStackTeamBonus(t *testing.T) {
	c := supplementalCore(t)
	engine.AttachEquipmentExtensions(c, map[string][]engine.Artifact{"amber": fourPieces("HeartOfTheFurnace"), "kaeya": fourPieces("HeartOfTheFurnace")})
	chars := c.Player.Chars()
	base0, base1 := chars[0].Stat(attributes.ATKP), chars[1].Stat(attributes.ATKP)
	ai := info.AttackInfo{ActorIndex: 1, AttackTag: attacks.AttackTagDirectStellarConduct}
	closeStat(t, chars[2].ReactBonus(ai), 0)
	c.Events.Emit(event.OnStellarConduct, c.Combat.Enemies()[0], &info.AttackEvent{Info: ai})
	closeStat(t, chars[1].Stat(attributes.ATKP), base1+.12)
	closeStat(t, chars[0].Stat(attributes.ATKP), base0)
	closeStat(t, chars[2].ReactBonus(ai), .50)
	ai.ActorIndex = 0
	c.Events.Emit(event.OnEnemyDamage, c.Combat.Enemies()[0], &info.AttackEvent{Info: ai}, 100.0, false)
	closeStat(t, chars[2].ReactBonus(ai), .50)
	c.F = 721
	closeStat(t, chars[2].ReactBonus(ai), 0)
	closeStat(t, chars[0].Stat(attributes.ATKP), base0)
}
func TestScarletProofOnlyTriggersFromWearersStellarSwirlAndExpires(t *testing.T) {
	c := supplementalCore(t)
	engine.AttachEquipmentExtensions(c, map[string][]engine.Artifact{"lisa": fourPieces("ScarletProof")})
	wearer := c.Player.Chars()[2]
	base := wearer.Stat(attributes.CR)
	ai := info.AttackInfo{ActorIndex: 2, AttackTag: attacks.AttackTagReactionStellarSwirl}
	c.Events.Emit(event.OnStellarConduct, c.Combat.Enemies()[0], &info.AttackEvent{Info: ai})
	closeStat(t, wearer.Stat(attributes.CR), base)
	c.Events.Emit(event.OnStellarSwirl, c.Combat.Enemies()[0], &info.AttackEvent{Info: ai})
	closeStat(t, wearer.Stat(attributes.CR), base+.16)
	closeStat(t, wearer.ReactBonus(ai), .4)
	c.F = 500
	c.Events.Emit(event.OnStellarSwirl, c.Combat.Enemies()[0], &info.AttackEvent{Info: ai})
	c.F = 601
	closeStat(t, wearer.ReactBonus(ai), .4)
	c.F = 1101
	closeStat(t, wearer.ReactBonus(ai), 0)
	closeStat(t, wearer.Stat(attributes.CR), base)
}

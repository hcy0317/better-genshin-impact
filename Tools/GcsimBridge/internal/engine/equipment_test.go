package engine_test

import (
	"strings"
	"testing"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

func number(value float64) *float64 { return &value }

func inventoryRequest() engine.Request {
	return engine.Request{
		SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17},
		Config:    strings.ReplaceAll(fixedConfig, "amber add stats hp=4780 atk=311 atk%=0.466 pyro%=0.466 cr=0.311;", ""),
		Inventory: &engine.InventoryRef{UID: "fixture", ScanSessionID: "scan-fixture-1", CatalogVersion: "manual-fixture-v1", SnapshotDigest: "fixed-fixture"},
		Equipment: map[string][]engine.Artifact{"amber": {
			{ScanIndex: 0, SlotKey: "flower", SetKey: "EmblemOfSeveredFate", MainStatKey: "hp", MainStatValue: number(4780), Substats: []engine.Substat{{Key: "critDMG_", Value: 21}}},
			{ScanIndex: 1, SlotKey: "plume", SetKey: "EmblemOfSeveredFate", MainStatKey: "atk", MainStatValue: number(311)},
			{ScanIndex: 2, SlotKey: "sands", SetKey: "EmblemOfSeveredFate", MainStatKey: "atk_", MainStatValue: number(46.6)},
			{ScanIndex: 3, SlotKey: "goblet", SetKey: "EmblemOfSeveredFate", MainStatKey: "pyro_dmg_", MainStatValue: number(46.6)},
			{ScanIndex: 4, SlotKey: "circlet", SetKey: "EmblemOfSeveredFate", MainStatKey: "critRate_", MainStatValue: number(31.1)},
		}},
	}
}

func TestInventoryStatsMatchNativeUnitsWithoutDoubleCounting(t *testing.T) {
	reference, err := engine.Evaluate(engine.Request{
		SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17},
		Config: fixedConfig + "\namber add stats cd=0.21;\namber add set=\"emblemofseveredfate\" count=5;\n",
	})
	if err != nil {
		t.Fatal(err)
	}
	request := inventoryRequest()
	actual, err := engine.Evaluate(request)
	if err != nil {
		t.Fatal(err)
	}
	if actual.MeanDPS != reference.MeanDPS {
		t.Fatalf("inventory units/set application: got %v, want %v", actual.MeanDPS, reference.MeanDPS)
	}
}

func TestNewStellarSetsApplyTheirTwoPieceBonusWithoutAReaction(t *testing.T) {
	for _, name := range []string{"HeartOfTheFurnace", "ScarletProof"} {
		t.Run(name, func(t *testing.T) {
			r := inventoryRequest()
			for i := range r.Equipment["amber"] {
				r.Equipment["amber"][i].SetKey = name
			}
			actual, err := engine.Evaluate(r)
			if err != nil {
				t.Fatal(err)
			}
			reference, err := engine.Evaluate(engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17}, Config: fixedConfig + "\namber add stats cd=0.21 atk%=0.18;"})
			if err != nil {
				t.Fatal(err)
			}
			if actual.MeanDPS != reference.MeanDPS {
				t.Fatalf("non-triggered set damage %v != %v", actual.MeanDPS, reference.MeanDPS)
			}
		})
	}
}

func TestInvalidInventoryIsNeverSilentlyAdjusted(t *testing.T) {
	for name, change := range map[string]func(*engine.Request){
		"double_stats":    func(r *engine.Request) { r.Config = fixedConfig },
		"missing_value":   func(r *engine.Request) { r.Equipment["amber"][0].MainStatValue = nil },
		"reused_instance": func(r *engine.Request) { r.Equipment["amber"][1].ScanIndex = 0 },
		"missing_slot":    func(r *engine.Request) { r.Equipment["amber"] = r.Equipment["amber"][:4] },
		"dormant":         func(r *engine.Request) { r.Equipment["amber"][0].Substats[0].Dormant = true },
		"unknown_set":     func(r *engine.Request) { r.Equipment["amber"][0].SetKey = "not-a-real-set" },
		"wrong_slot":      func(r *engine.Request) { r.Equipment["amber"][0].MainStatKey = "critRate_" },
	} {
		t.Run(name, func(t *testing.T) {
			r := inventoryRequest()
			change(&r)
			if _, err := engine.Evaluate(r); err == nil {
				t.Fatal("invalid inventory was accepted")
			}
		})
	}
}

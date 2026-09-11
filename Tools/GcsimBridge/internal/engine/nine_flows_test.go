package engine_test

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/nativeflow"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/rotation"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"testing"
)

func TestNineOriginalStrategiesExecuteOnRealGcsim(t *testing.T) {
	files, err := filepath.Glob("testdata/nine/*.json")
	if err != nil || len(files) != 9 {
		t.Fatalf("nine fixtures required: %v", err)
	}
	weapons := map[string]string{"zhongli": "blacktassel", "furina": "favoniussword", "jean": "favoniussword", "neuvillette": "prototypeamber", "nahida": "prototypeamber", "sangonomiyakokomi": "prototypeamber", "bennett": "favoniussword", "xiangling": "favoniuslance", "navia": "favoniusgreatsword", "kaedeharakazuha": "favoniussword", "kamisatoayaka": "favoniussword", "raidenshogun": "favoniuslance", "fischl": "favoniuswarbow", "kukishinobu": "favoniussword"}
	for _, file := range files {
		t.Run(filepath.Base(file), func(t *testing.T) {
			data, err := os.ReadFile(file)
			if err != nil {
				t.Fatal(err)
			}
			var p nativeflow.Program
			if err = json.Unmarshal(data, &p); err != nil {
				t.Fatal(err)
			}
			members := map[string]bool{}
			nodes := append([]nativeflow.Node(nil), p.Root...)
			for _, b := range p.Blocks {
				nodes = append(nodes, b.Nodes...)
			}
			for _, n := range nodes {
				if n.Character != "" {
					members[n.Character] = true
				}
			}
			keys := []string{}
			for key := range members {
				keys = append(keys, key)
			}
			sort.Strings(keys)
			var config strings.Builder
			config.WriteString("options duration=65;target lvl=100 resist=0.1 radius=2 pos=0,2.4;\n")
			for _, key := range keys {
				weapon, ok := weapons[key]
				if !ok {
					t.Fatal(key)
				}
				fmt.Fprintf(&config, "%s char lvl=90/90 cons=0 talent=6,6,6;%s add weapon=\"%s\" refine=1 lvl=90/90;%s add stats hp=4780 atk=311 er=1 cr=0.311 cd=0.622;\n", key, key, weapon, key)
			}
			config.WriteString("active zhongli;")
			counted, countErr := engine.Evaluate(engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17}, NativeFlow: &p, Config: config.String(), AutoRounds: true, RoundCount: 3})
			if countErr != nil {
				t.Fatalf("three-round original: %v", countErr)
			}
			if len(counted.RoundTraces) != 1 || len(counted.RoundTraces[0].Rounds) != 3 || !counted.Validation.Complete {
				t.Fatal("original strategy did not complete exactly three rounds")
			}
			report, err := engine.Evaluate(engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17}, NativeFlow: &p, Config: config.String(), AutoRounds: true})
			if err != nil {
				t.Fatal(err)
			}
			if report.MeanDPS <= 0 {
				t.Fatal("no real damage")
			}
			if report.NativeQuality == nil || !report.NativeQuality.Complete || report.NativeQuality.Samples != 1 {
				t.Fatal("missing complete native quality evidence")
			}
			compact, err := engine.Evaluate(engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Seeds: []int64{17}, NativeFlow: &p, Config: config.String(), AutoRounds: true, CompactSamples: true})
			if err != nil {
				t.Fatal(err)
			}
			if report.MeanDPS != compact.MeanDPS || !reflect.DeepEqual(report.ScoredDPS, compact.ScoredDPS) ||
				!reflect.DeepEqual(report.NativeQuality, compact.NativeQuality) || !reflect.DeepEqual(report.NativeFlowTraces, compact.NativeFlowTraces) ||
				!reflect.DeepEqual(report.Validation, compact.Validation) || !reflect.DeepEqual(report.Metrics, compact.Metrics) ||
				!reflect.DeepEqual(report.RoundTraces, compact.RoundTraces) || !reflect.DeepEqual(report.EnergyWindows, compact.EnergyWindows) {
				t.Fatal("compact collectors changed native scoring/quality/evidence")
			}
			actions := map[string]int{}
			for _, trace := range report.NativeFlowTraces {
				for _, event := range trace.Events {
					if event.Kind == "action" {
						actions[event.Detail]++
					}
				}
			}
			t.Logf("DPS %.2f; actions %v", report.MeanDPS, actions)
			if strings.Contains(file, "草") || strings.Contains(file, "雷") {
				if actions["charge"] == 0 {
					t.Fatal("required charge branch never ran")
				}
			}
			if strings.Contains(file, "冰") && actions["dash"] == 0 {
				t.Fatal("Ayaka dash never ran")
			}
			optimized, err := rotation.Optimize(context.Background(), rotation.Request{Base: engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, NativeFlow: &p, Config: config.String(), AutoRounds: true}, SearchSeeds: []int64{17, 29}, ValidationSeeds: []int64{31, 37}, Budget: 48}, func(ctx context.Context, r engine.Request) (engine.Report, error) { return engine.Evaluate(r) })
			if err != nil {
				t.Fatal(err)
			}
			if optimized.Report == nil || !optimized.Report.Validation.Complete || optimized.Report.Validation.State != "passed" {
				t.Fatalf("native optimization not qualified: %s %v", optimized.Status, optimized.Issues)
			}
			t.Logf("optimized %s DPS %.2f", optimized.Status, optimized.Report.MeanDPS)
			t.Logf("structural edits: %v", optimized.NativeEdits)
			if directory := os.Getenv("BGI_NATIVE_RESULT_DIR"); directory != "" {
				if err := os.MkdirAll(directory, 0700); err != nil {
					t.Fatal(err)
				}
				data, err := json.Marshal(optimized)
				if err != nil {
					t.Fatal(err)
				}
				if err = os.WriteFile(filepath.Join(directory, filepath.Base(file)), data, 0600); err != nil {
					t.Fatal(err)
				}
			}
		})
	}
}

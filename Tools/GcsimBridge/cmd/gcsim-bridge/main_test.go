package main

import (
	"bytes"
	"context"
	"encoding/json"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"os"
	"strings"
	"testing"
)

func TestMain(m *testing.M) {
	if len(os.Args) > 1 && os.Args[1] == "--worker" {
		os.Exit(worker(os.Stdin, os.Stdout))
	}
	os.Exit(m.Run())
}

const config = `options duration=5; target lvl=90 resist=0.1;
amber char lvl=90/90 cons=0 talent=6,6,6; amber add weapon="huntersbow" refine=1 lvl=90/90;
active amber; while 1 { amber attack; }`

func TestCLIProducesARealBoundedSimulation(t *testing.T) {
	input, _ := json.Marshal(map[string]any{"evaluation": engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: config, Seeds: []int64{17}}, "limits": Budget{WallTimeMS: 5000}})
	var output bytes.Buffer
	code := RunCLI(context.Background(), bytes.NewReader(input), &output)
	var response struct {
		Status string         `json:"status"`
		Report *engine.Report `json:"report"`
	}
	if err := json.Unmarshal(output.Bytes(), &response); err != nil {
		t.Fatal(err)
	}
	if code != 0 || response.Status != "completed" || response.Report == nil || response.Report.MeanDPS <= 0 || response.Report.Validation.State != "passed" {
		t.Fatalf("CLI did not produce a verified sample: code=%d %s", code, output.String())
	}
}

func TestCLIStopsNonAdvancingGcsimScript(t *testing.T) {
	infinite := strings.ReplaceAll(config, "while 1 { amber attack; }", "while 1 {}")
	input, _ := json.Marshal(jobRequest{Evaluation: engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: infinite, Seeds: []int64{17}}, Limits: Budget{WallTimeMS: 1500, MemoryMiB: 128, OutputKiB: 64}})
	var output bytes.Buffer
	code := RunCLI(context.Background(), bytes.NewReader(input), &output)
	var response reply
	if err := json.Unmarshal(output.Bytes(), &response); err != nil {
		t.Fatal(err)
	}
	if code == 0 || response.Status != "timeout" || response.Report != nil || response.Resources == nil {
		t.Fatalf("nonadvancing script escaped: %s", output.String())
	}
	if response.Resources.CPUMS < 50 || response.Resources.ElapsedMS > 3000 || response.Resources.Isolation != "windows_job" {
		t.Fatalf("missing real execution/termination evidence: %+v", response.Resources)
	}
}

func TestCLIStrictInputDoesNotStartAWorker(t *testing.T) {
	for _, input := range []string{`{"unexpected":true}`, `{} {}`, `{"limits":{"wallTimeMs":9223372036854775807}}`} {
		var output bytes.Buffer
		if RunCLI(context.Background(), strings.NewReader(input), &output) == 0 {
			t.Fatal("invalid request succeeded")
		}
		var response reply
		if json.Unmarshal(output.Bytes(), &response) != nil || response.Status != "invalid_input" || response.Resources != nil {
			t.Fatalf("invalid input launched a worker: %s", output.String())
		}
	}
}

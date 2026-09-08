package main

import (
	"bytes"
	"context"
	"encoding/json"
	"sort"
	"strings"
	"testing"
	"time"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
)

// This is a controlled timer probe, not an assertion about live game/frame behavior.
func timerProbe(work func() reply) ([]time.Duration, reply) {
	done := make(chan reply, 1)
	go func() { done <- work() }()
	ticker := time.NewTicker(20 * time.Millisecond)
	defer ticker.Stop()
	var lateness []time.Duration
	for {
		select {
		case tick := <-ticker.C:
			lateness = append(lateness, time.Since(tick))
		case result := <-done:
			return lateness, result
		}
	}
}

func percentile95(values []time.Duration) time.Duration {
	if len(values) == 0 {
		return 0
	}
	sort.Slice(values, func(i, j int) bool { return values[i] < values[j] })
	return values[(len(values)-1)*95/100]
}

func TestLowInterferenceTimerProbe(t *testing.T) {
	baseline, _ := timerProbe(func() reply { timer := time.NewTimer(400 * time.Millisecond); <-timer.C; return reply{} })
	seeds := make([]int64, 64)
	for i := range seeds {
		seeds[i] = int64(i + 1)
	}
	input, _ := json.Marshal(jobRequest{Evaluation: engine.Request{SchemaVersion: "1", EngineRevision: engine.Revision, Config: strings.ReplaceAll(config, "duration=5", "duration=60"), Seeds: seeds}, Limits: Budget{WallTimeMS: 10000, OutputKiB: 16384}})
	loaded, response := timerProbe(func() reply {
		var output bytes.Buffer
		RunCLI(context.Background(), bytes.NewReader(input), &output)
		var result reply
		_ = json.Unmarshal(output.Bytes(), &result)
		return result
	})
	if response.Status != "completed" || response.Resources == nil || len(loaded) == 0 {
		t.Fatalf("probe produced no complete calculation/timing evidence: %s %s", response.Status, response.Error)
	}
	if response.Resources.PeakMemoryBytes == nil {
		t.Fatal("OS memory accounting unavailable")
	}
	t.Logf("controlled 20ms timer: baseline p95=%s (n=%d), loaded p95=%s (n=%d); worker elapsed=%dms CPU=%dms peak=%d bytes; not live-game evidence", percentile95(baseline), len(baseline), percentile95(loaded), len(loaded), response.Resources.ElapsedMS, response.Resources.CPUMS, *response.Resources.PeakMemoryBytes)
}

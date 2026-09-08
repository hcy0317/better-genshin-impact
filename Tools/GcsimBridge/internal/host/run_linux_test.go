//go:build linux

package host_test

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
	"os"
	"strings"
	"testing"
	"time"
)

func TestMain(m *testing.M) {
	if len(os.Args) > 1 && os.Args[1] == "--worker" {
		host.WorkerReady()
		var r struct {
			Mode string `json:"mode"`
		}
		if json.NewDecoder(os.Stdin).Decode(&r) != nil {
			os.Exit(2)
		}
		switch r.Mode {
		case "sleep":
			for {
				time.Sleep(time.Hour)
			}
		case "allocate":
			var chunks [][]byte
			for range 48 {
				b := make([]byte, 8<<20)
				for i := 0; i < len(b); i += 4096 {
					b[i] = 1
				}
				chunks = append(chunks, b)
			}
			os.Stdout.Write([]byte{chunks[len(chunks)-1][0]})
			os.Exit(0)
		case "flood":
			for {
				os.Stdout.Write(bytes.Repeat([]byte("x"), 8192))
			}
		case "echo":
			os.Stdout.WriteString("ready")
			os.Exit(0)
		}
		os.Exit(3)
	}
	os.Exit(m.Run())
}
func TestLinuxLimitsAllowValidWorker(t *testing.T) {
	r, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"echo"}`), host.Limits{MemoryBytes: 256 << 20})
	if err != nil || string(r.Stdout) != "ready" {
		t.Fatalf("valid bounded worker failed: %+v %v", r, err)
	}
}
func TestLinuxDeadlineAndOutputLimitStopOwnedWorker(t *testing.T) {
	for _, mode := range []string{"sleep", "flood"} {
		r, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"`+mode+`"}`), host.Limits{WallTime: 500 * time.Millisecond, MemoryBytes: 256 << 20, OutputBytes: 1024})
		if mode == "sleep" && !errors.Is(err, context.DeadlineExceeded) || mode == "flood" && !errors.Is(err, host.ErrOutputLimit) {
			t.Fatalf("worker escaped %s: %+v %v", mode, r, err)
		}
	}
}
func TestLinuxDataMemoryBudgetStopsAllocation(t *testing.T) {
	r, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"allocate"}`), host.Limits{MemoryBytes: 256 << 20, WallTime: 5 * time.Second})
	if err == nil || errors.Is(err, context.DeadlineExceeded) || r.PID == 0 || !strings.Contains(strings.ToLower(string(r.Stderr)), "memory") {
		t.Fatalf("allocation escaped or lacked memory-limit evidence: %+v %v", r, err)
	}
}

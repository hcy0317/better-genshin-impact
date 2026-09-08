//go:build windows

package host_test

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"strings"
	"syscall"
	"testing"
	"time"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
	"golang.org/x/sys/windows"
)

func TestMain(m *testing.M) {
	if len(os.Args) > 1 && os.Args[1] == "--worker" {
		var request struct {
			Mode string `json:"mode"`
		}
		if json.NewDecoder(os.Stdin).Decode(&request) != nil {
			os.Exit(2)
		}
		switch request.Mode {
		case "sleep":
			for {
				time.Sleep(time.Hour)
			}
		case "flood":
			data := bytes.Repeat([]byte("x"), 8192)
			for {
				_, _ = os.Stdout.Write(data)
				_, _ = os.Stderr.Write(data)
			}
		case "allocate":
			var chunks [][]byte
			for range 20 {
				chunk := make([]byte, 8<<20)
				for i := 0; i < len(chunk); i += 4096 {
					chunk[i] = 1
				}
				chunks = append(chunks, chunk)
			}
			_, _ = os.Stdout.Write([]byte{chunks[len(chunks)-1][0]})
			os.Exit(0)
		case "echo":
			_, _ = os.Stdout.WriteString("ready")
			os.Exit(0)
		default:
			os.Exit(3)
		}
	}
	os.Exit(m.Run())
}

func TestCancellationStopsWorker(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	timer := time.AfterFunc(200*time.Millisecond, cancel)
	defer timer.Stop()
	result, err := host.Run(ctx, os.Args[0], []byte(`{"mode":"sleep"}`), host.Limits{WallTime: 5 * time.Second})
	if !errors.Is(err, context.Canceled) || result.PID == 0 || isRunning(t, result.PID) {
		t.Fatalf("cancellation left an active worker: pid=%d, err=%v", result.PID, err)
	}
}

func TestCombinedOutputIsBounded(t *testing.T) {
	result, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"flood"}`), host.Limits{WallTime: 3 * time.Second, OutputBytes: 2048})
	if !errors.Is(err, host.ErrOutputLimit) {
		t.Fatalf("output flood: %v", err)
	}
	if len(result.Stdout)+len(result.Stderr) > 2048 || isRunning(t, result.PID) {
		t.Fatal("output or worker escaped its bound")
	}
}

func TestOSMemoryLimitRejectsExcessAllocation(t *testing.T) {
	result, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"allocate"}`), host.Limits{
		WallTime: 5 * time.Second, MemoryBytes: 64 << 20, OutputBytes: 1 << 20,
	})
	if err == nil || errors.Is(err, context.DeadlineExceeded) || isRunning(t, result.PID) {
		t.Fatalf("allocation was not stopped by the memory boundary: %v", err)
	}
	if result.PeakMemoryBytes == nil || *result.PeakMemoryBytes == 0 || *result.PeakMemoryBytes > 64<<20 {
		t.Fatalf("invalid OS memory-limit evidence: %+v", result.PeakMemoryBytes)
	}
}

func TestSuccessfulWorkerAndAlreadyCancelledRequest(t *testing.T) {
	result, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"echo"}`), host.Limits{})
	if err != nil || string(result.Stdout) != "ready" || result.ExitCode != 0 {
		t.Fatalf("worker result: %+v, %v", result, err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	result, err = host.Run(ctx, os.Args[0], []byte(`{"mode":"sleep"}`), host.Limits{})
	if !errors.Is(err, context.Canceled) || result.PID != 0 {
		t.Fatal("cancelled input started a process")
	}
}

func isRunning(t *testing.T, pid int) bool {
	t.Helper()
	h, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return false
	}
	defer windows.CloseHandle(h)
	var code uint32
	if err := windows.GetExitCodeProcess(h, &code); err != nil {
		t.Fatal(err)
	}
	return code == 259 // STILL_ACTIVE
}

func TestDeadlineStopsOnlyTheOwnedWorker(t *testing.T) {
	guard := exec.Command(os.Args[0], "--worker")
	guard.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: windows.CREATE_NO_WINDOW}
	guard.Stdin = strings.NewReader(`{"mode":"sleep"}`)
	if err := guard.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { _ = guard.Process.Kill(); _ = guard.Wait() }()

	started := time.Now()
	result, err := host.Run(context.Background(), os.Args[0], []byte(`{"mode":"sleep"}`), host.Limits{
		WallTime: 250 * time.Millisecond, MemoryBytes: 128 << 20, OutputBytes: 4096,
	})
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("got %v; want wall-clock timeout", err)
	}
	if result.PID == 0 || isRunning(t, result.PID) {
		t.Fatal("owned worker survived the deadline")
	}
	if !isRunning(t, guard.Process.Pid) {
		t.Fatal("unrelated process was terminated")
	}
	if time.Since(started) > 3*time.Second {
		t.Fatal("cleanup exceeded its bounded grace period")
	}
}

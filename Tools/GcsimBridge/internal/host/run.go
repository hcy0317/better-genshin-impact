// Package host owns the lifetime and resource budget of one gcsim worker.
package host

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Limits struct {
	WallTime    time.Duration
	MemoryBytes uint64
	OutputBytes int
}

type Output struct {
	PID             int
	Stdout          []byte
	Stderr          []byte
	Elapsed         time.Duration
	CPUTime         time.Duration
	ExitCode        int
	PeakMemoryBytes *uint64
	Limits          Limits
	Isolation       string
}

var ErrOutputLimit = errors.New("worker output limit exceeded")

const readyMarker = "GCSIM_TRUSTED_RUNTIME_READY\n"

// The worker has finished trusted Go/package initialization, but has not read
// any untrusted configuration. Linux virtual/data limits are attached here.
func WorkerReady() {
	if runtime.GOOS == "linux" {
		debug.SetMaxThreads(16)
		_, _ = os.Stderr.WriteString(readyMarker)
	}
}

func Run(ctx context.Context, executable string, input []byte, limits Limits) (output Output, err error) {
	return RunWorker(ctx, executable, "--worker", input, limits)
}

func RunWorker(ctx context.Context, executable, workerMode string, input []byte, limits Limits) (output Output, err error) {
	// Linux PDEATHSIG is tied to the creator thread. Keep it alive until reap.
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	if workerMode != "--worker" && workerMode != "--optimizer-worker" && workerMode != "--rotation-worker" {
		return output, errors.New("unsupported worker mode")
	}
	started := time.Now()
	defer func() { output.Elapsed = time.Since(started) }()
	output.ExitCode = -1
	if err := ctx.Err(); err != nil {
		return output, err
	}
	if limits.WallTime == 0 {
		limits.WallTime = 30 * time.Second
	}
	if limits.MemoryBytes == 0 {
		limits.MemoryBytes = 512 << 20
	}
	if limits.OutputBytes == 0 {
		limits.OutputBytes = 16 << 20
	}
	if !filepath.IsAbs(executable) || len(input) == 0 || len(input) > 2<<20 ||
		limits.WallTime <= 0 || limits.WallTime > 2*time.Minute ||
		limits.MemoryBytes < 64<<20 || limits.MemoryBytes > 1<<30 ||
		limits.OutputBytes < 1024 || limits.OutputBytes > 64<<20 {
		return output, errors.New("invalid worker path, input or resource budget")
	}
	output.Limits = limits
	ctx, cancel := context.WithTimeout(ctx, limits.WallTime)
	defer cancel()
	job, err := newWorkerJob(limits.MemoryBytes)
	if err != nil {
		return output, err
	}
	defer job.Close()
	capture := &boundedCapture{remaining: limits.OutputBytes, exceeded: make(chan struct{}, 1), ready: make(chan struct{})}
	cmd := exec.Command(executable, workerMode)
	configureProcess(cmd)
	for _, entry := range os.Environ() {
		key, _, _ := strings.Cut(entry, "=")
		if key != "GOMAXPROCS" && key != "GOMEMLIMIT" {
			cmd.Env = append(cmd.Env, entry)
		}
	}
	cmd.Env = append(cmd.Env, "GOMAXPROCS=1", "GOMEMLIMIT="+strconv.FormatUint(limits.MemoryBytes*3/4, 10)+"B")
	cmd.Stdout = captureWriter{capture: capture}
	cmd.Stderr = captureWriter{capture: capture, stderr: true}
	cmd.WaitDelay = time.Second
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return output, err
	}
	defer stdin.Close()
	if err := cmd.Start(); err != nil {
		return output, err
	}
	output.PID = cmd.Process.Pid
	if runtime.GOOS == "linux" {
		select {
		case <-capture.ready:
		case <-ctx.Done():
			_ = cmd.Process.Kill()
			_ = cmd.Wait()
			return output, ctx.Err()
		case <-capture.exceeded:
			_ = cmd.Process.Kill()
			_ = cmd.Wait()
			return output, ErrOutputLimit
		}
	}
	// No untrusted configuration enters the worker until OS limits are attached.
	// cmd retains its process handle until Wait, so Windows cannot recycle this PID.
	if err := job.Assign(cmd.Process.Pid); err != nil {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
		return output, fmt.Errorf("isolate worker: %w", err)
	}
	output.Isolation = job.Isolation()
	inputDone := make(chan error, 1)
	go func() { _, err := stdin.Write(input); _ = stdin.Close(); inputDone <- err }()
	waited := make(chan error, 1)
	go func() { waited <- cmd.Wait() }()
	select {
	case err = <-waited:
	case <-ctx.Done():
		err = ctx.Err()
		_ = job.Terminate()
		_ = cmd.Process.Kill()
		<-waited
	case <-capture.exceeded:
		err = ErrOutputLimit
		_ = job.Terminate()
		_ = cmd.Process.Kill()
		<-waited
	}
	_ = stdin.Close()
	if writeErr := <-inputDone; err == nil && writeErr != nil {
		err = fmt.Errorf("send worker input: %w", writeErr)
	}
	output.Stdout, output.Stderr = capture.stdout.Bytes(), capture.stderr.Bytes()
	output.ExitCode = cmd.ProcessState.ExitCode()
	output.CPUTime = cmd.ProcessState.UserTime() + cmd.ProcessState.SystemTime()
	output.PeakMemoryBytes = job.PeakMemory()
	return output, err
}

type boundedCapture struct {
	mu             sync.Mutex
	remaining      int
	stdout, stderr bytes.Buffer
	exceeded       chan struct{}
	ready          chan struct{}
	readySent      bool
}

type captureWriter struct {
	capture *boundedCapture
	stderr  bool
}

func (writer captureWriter) Write(data []byte) (int, error) {
	c := writer.capture
	c.mu.Lock()
	defer c.mu.Unlock()
	n := min(len(data), c.remaining)
	if writer.stderr {
		_, _ = c.stderr.Write(data[:n])
		if c.ready != nil && !c.readySent && bytes.Contains(c.stderr.Bytes(), []byte(readyMarker)) {
			c.readySent = true
			close(c.ready)
		}
	} else {
		_, _ = c.stdout.Write(data[:n])
	}
	c.remaining -= n
	if n < len(data) {
		select {
		case c.exceeded <- struct{}{}:
		default:
		}
		return n, ErrOutputLimit
	}
	return n, nil
}

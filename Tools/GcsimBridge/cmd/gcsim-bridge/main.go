package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/signal"
	"runtime"
	"time"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
)

type Budget struct {
	WallTimeMS int64  `json:"wallTimeMs"`
	MemoryMiB  uint64 `json:"memoryMiB"`
	OutputKiB  int    `json:"outputKiB"`
}

type jobRequest struct {
	Evaluation engine.Request `json:"evaluation"`
	Limits     Budget         `json:"limits"`
}

type reply struct {
	Status    string         `json:"status"`
	Report    *engine.Report `json:"report,omitempty"`
	Error     string         `json:"error,omitempty"`
	Resources *resources     `json:"resources,omitempty"`
}

type resources struct {
	WorkerPID        int     `json:"workerPid"`
	ExitCode         int     `json:"exitCode"`
	ElapsedMS        int64   `json:"elapsedMs"`
	CPUMS            int64   `json:"cpuMs"`
	PeakMemoryBytes  *uint64 `json:"peakMemoryBytes,omitempty"`
	MemoryLimitBytes uint64  `json:"memoryLimitBytes"`
	OutputLimitBytes int     `json:"outputLimitBytes"`
	Isolation        string  `json:"isolation"`
}

func decode(input io.Reader, value any) error {
	data, err := io.ReadAll(io.LimitReader(input, (2<<20)+1))
	if err != nil {
		return err
	}
	if len(data) > 2<<20 {
		return errors.New("request exceeds 2 MiB")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(value); err != nil {
		return err
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		return errors.New("expected exactly one JSON request")
	}
	return nil
}

func writeReply(output io.Writer, response reply) int {
	if err := json.NewEncoder(output).Encode(response); err != nil {
		return 2
	}
	if response.Status != "completed" {
		return 2
	}
	return 0
}

// RunCLI is the single-request JSON process boundary used by desktop/service callers.
func RunCLI(ctx context.Context, input io.Reader, output io.Writer) int {
	var job jobRequest
	if err := decode(input, &job); err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	if job.Limits.WallTimeMS < 0 || job.Limits.WallTimeMS > 120000 || job.Limits.MemoryMiB > 1024 || job.Limits.OutputKiB < 0 || job.Limits.OutputKiB > 65536 {
		return writeReply(output, reply{Status: "invalid_input", Error: "invalid job budget"})
	}
	executable, err := os.Executable()
	if err != nil {
		return writeReply(output, reply{Status: "failed", Error: err.Error()})
	}
	payload, err := json.Marshal(job.Evaluation)
	if err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	result, runErr := host.Run(ctx, executable, payload, host.Limits{WallTime: time.Duration(job.Limits.WallTimeMS) * time.Millisecond, MemoryBytes: job.Limits.MemoryMiB << 20, OutputBytes: job.Limits.OutputKiB << 10})
	response := reply{}
	if len(result.Stdout) > 0 {
		_ = json.Unmarshal(result.Stdout, &response)
	}
	if runErr != nil {
		switch {
		case errors.Is(runErr, context.DeadlineExceeded):
			response = reply{Status: "timeout", Error: "owned calculation exceeded its wall-clock budget"}
		case errors.Is(runErr, context.Canceled):
			response = reply{Status: "cancelled", Error: "owned calculation was cancelled"}
		case errors.Is(runErr, host.ErrOutputLimit):
			response = reply{Status: "output_limit", Error: runErr.Error()}
		default:
			if response.Error == "" {
				response.Error = runErr.Error()
				if len(result.Stderr) > 0 {
					response.Error += "; " + string(result.Stderr[:min(2048, len(result.Stderr))])
				}
			}
			response.Status = "failed"
			response.Report = nil
		}
	} else if response.Status != "completed" || response.Report == nil || response.Report.EngineRevision != engine.Revision || len(response.Report.Samples) != len(job.Evaluation.Seeds) {
		response = reply{Status: "failed", Error: "invalid or incomplete worker response"}
	}
	response.Resources = &resources{WorkerPID: result.PID, ExitCode: result.ExitCode, ElapsedMS: result.Elapsed.Milliseconds(), CPUMS: result.CPUTime.Milliseconds(), PeakMemoryBytes: result.PeakMemoryBytes, MemoryLimitBytes: result.Limits.MemoryBytes, OutputLimitBytes: result.Limits.OutputBytes, Isolation: result.Isolation}
	return writeReply(output, response)
}

func worker(input io.Reader, output io.Writer) (code int) {
	host.WorkerReady()
	// Keep upstream diagnostics away from the machine-readable stdout channel.
	os.Stdout = os.Stderr
	response := reply{Status: "failed"}
	defer func() {
		if failure := recover(); failure != nil {
			response = reply{Status: "failed", Error: fmt.Sprintf("gcsim worker panic: %v", failure)}
		}
		code = writeReply(output, response)
	}()
	var request engine.Request
	if err := decode(input, &request); err != nil {
		response.Error = err.Error()
		return 2
	}
	report, err := engine.Evaluate(request)
	if err != nil {
		response.Error = err.Error()
		return 2
	}
	response = reply{Status: "completed", Report: &report}
	return 0
}

func main() {
	runtime.GOMAXPROCS(1)
	sdk, err := verifySDK()
	if err != nil {
		os.Exit(writeReply(os.Stdout, reply{Status: "invalid_engine", Error: err.Error()}))
	}
	if len(os.Args) == 2 && os.Args[1] == "--capabilities" {
		capabilities := engine.Capabilities()
		capabilities["sdk"] = sdk
		if json.NewEncoder(os.Stdout).Encode(capabilities) != nil {
			os.Exit(2)
		}
		return
	}
	if len(os.Args) == 2 && os.Args[1] == "--catalog" {
		if json.NewEncoder(os.Stdout).Encode(engine.Catalog()) != nil {
			os.Exit(2)
		}
		return
	}
	if len(os.Args) > 1 && os.Args[1] == "--worker" {
		os.Exit(worker(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--optimizer-worker" {
		os.Exit(optimizationWorker(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--rotation-worker" {
		os.Exit(rotationWorker(os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--rotation" {
		ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
		defer cancel()
		os.Exit(rotationCLI(ctx, os.Stdin, os.Stdout))
	}
	if len(os.Args) == 2 && os.Args[1] == "--optimize" {
		ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
		defer cancel()
		os.Exit(optimizationCLI(ctx, os.Stdin, os.Stdout))
	}
	if len(os.Args) > 1 {
		os.Exit(writeReply(os.Stdout, reply{Status: "invalid_input", Error: "expected JSON on stdin or --capabilities"}))
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()
	os.Exit(RunCLI(ctx, os.Stdin, os.Stdout))
}

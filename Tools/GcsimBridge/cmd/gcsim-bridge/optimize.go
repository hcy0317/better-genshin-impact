package main

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/optimizer"
	"io"
	"os"
	"time"
)

type optimizationJob struct {
	Optimization optimizer.Request `json:"optimization"`
	Limits       Budget            `json:"limits"`
}

func optimizationCLI(ctx context.Context, input io.Reader, output io.Writer) int {
	var job optimizationJob
	if err := decode(input, &job); err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	exe, err := os.Executable()
	if err != nil {
		return writeReply(output, reply{Status: "failed", Error: err.Error()})
	}
	data, err := json.Marshal(job.Optimization)
	if err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	result, err := host.RunWorker(ctx, exe, "--optimizer-worker", data, host.Limits{WallTime: time.Duration(job.Limits.WallTimeMS) * time.Millisecond, MemoryBytes: job.Limits.MemoryMiB << 20, OutputBytes: job.Limits.OutputKiB << 10})
	if err != nil {
		return writeReply(output, reply{Status: "failed", Error: fmt.Sprintf("bounded optimizer: %v", err)})
	}
	if _, err = output.Write(result.Stdout); err != nil {
		return 2
	}
	return 0
}

func optimizationWorker(input io.Reader, output io.Writer) (code int) {
	os.Stdout = os.Stderr
	defer func() {
		if r := recover(); r != nil {
			code = writeReply(output, reply{Status: "failed", Error: fmt.Sprint(r)})
		}
	}()
	var request optimizer.Request
	if err := decode(input, &request); err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	result, err := optimizer.Optimize(context.Background(), request, func(ctx context.Context, r engine.Request) (engine.Report, error) {
		if err := ctx.Err(); err != nil {
			return engine.Report{}, err
		}
		return engine.Evaluate(r)
	})
	if err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	if json.NewEncoder(output).Encode(map[string]any{"status": "completed", "result": result}) != nil {
		return 2
	}
	return 0
}

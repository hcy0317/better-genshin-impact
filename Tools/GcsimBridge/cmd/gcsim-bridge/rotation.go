package main

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/engine"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/rotation"
	"io"
	"os"
	"time"
)

type rotationJob struct {
	Rotation rotation.Request `json:"rotation"`
	Limits   Budget           `json:"limits"`
}

func rotationCLI(ctx context.Context, input io.Reader, output io.Writer) int {
	var job rotationJob
	if err := decode(input, &job); err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	exe, err := os.Executable()
	if err != nil {
		return writeReply(output, reply{Status: "failed", Error: err.Error()})
	}
	payload, err := json.Marshal(job.Rotation)
	if err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	r, err := host.RunWorker(ctx, exe, "--rotation-worker", payload, host.Limits{WallTime: time.Duration(job.Limits.WallTimeMS) * time.Millisecond, MemoryBytes: job.Limits.MemoryMiB << 20, OutputBytes: job.Limits.OutputKiB << 10})
	if err != nil {
		return writeReply(output, workerFailure("bounded rotation search", r, err))
	}
	if _, err = output.Write(r.Stdout); err != nil {
		return 2
	}
	return 0
}
func rotationWorker(input io.Reader, output io.Writer) (code int) {
	host.WorkerReady()
	os.Stdout = os.Stderr
	defer func() {
		if r := recover(); r != nil {
			code = writeReply(output, reply{Status: "failed", Error: fmt.Sprint(r)})
		}
	}()
	var request rotation.Request
	if err := decode(input, &request); err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	result, err := rotation.Optimize(context.Background(), request, func(_ context.Context, r engine.Request) (engine.Report, error) { return engine.Evaluate(r) })
	if err != nil {
		return writeReply(output, reply{Status: "invalid_input", Error: err.Error()})
	}
	if json.NewEncoder(output).Encode(map[string]any{"status": "completed", "result": result}) != nil {
		return 2
	}
	return 0
}

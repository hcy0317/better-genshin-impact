package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
)

// Only the worker's dedicated JSON failure channel can describe a normal
// failure. Host-enforced termination always takes precedence over its output.
func workerFailure(label string, result host.Output, runErr error) reply {
	response := reply{Status: "failed", Error: fmt.Sprintf("%s: %v", label, runErr)}
	switch {
	case errors.Is(runErr, context.DeadlineExceeded):
		response = reply{Status: "timeout", Error: "计算达到墙钟时限，已停止计算子进程"}
	case errors.Is(runErr, context.Canceled):
		response = reply{Status: "cancelled", Error: "计算已取消"}
	case errors.Is(runErr, host.ErrOutputLimit):
		response = reply{Status: "output_limit", Error: "计算输出超过大小限制，已停止计算子进程"}
	case result.PeakMemoryBytes != nil && result.Limits.MemoryBytes > 0 && *result.PeakMemoryBytes >= result.Limits.MemoryBytes:
		response = reply{Status: "memory_limit", Error: "计算触及内存上限，已停止计算子进程；请检查脚本中的无限等待或缩小计算范围"}
	case result.ExitCode > 0 && len(result.Stdout) <= 64*1024:
		var failure reply
		if decode(bytes.NewReader(result.Stdout), &failure) == nil &&
			(failure.Status == "invalid_input" || failure.Status == "failed") &&
			strings.TrimSpace(failure.Error) != "" && len(failure.Error) <= 8192 &&
			failure.Report == nil && failure.Resources == nil {
			response = failure
		}
	}
	response.Resources = &resources{WorkerPID: result.PID, ExitCode: result.ExitCode,
		ElapsedMS: result.Elapsed.Milliseconds(), CPUMS: result.CPUTime.Milliseconds(),
		PeakMemoryBytes: result.PeakMemoryBytes, MemoryLimitBytes: result.Limits.MemoryBytes,
		OutputLimitBytes: result.Limits.OutputBytes, Isolation: result.Isolation}
	return response
}

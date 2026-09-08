package main

import (
	"context"
	"errors"
	"testing"

	"github.com/hcy0317/better-genshin-impact/tools/gcsimbridge/internal/host"
)

func TestWorkerFailureKeepsHostLimitsAuthoritative(t *testing.T) {
	peak := uint64(768 << 20)
	output := host.Output{ExitCode: 2, PeakMemoryBytes: &peak, Limits: host.Limits{MemoryBytes: peak},
		Stdout: []byte(`{"status":"failed","error":"untrusted reason"}`)}
	for _, test := range []struct {
		err    error
		status string
	}{
		{context.DeadlineExceeded, "timeout"},
		{context.Canceled, "cancelled"},
		{host.ErrOutputLimit, "output_limit"},
		{errors.New("exit status 2"), "memory_limit"},
	} {
		response := workerFailure("bounded optimizer", output, test.err)
		if response.Status != test.status || response.Error == "untrusted reason" || response.Resources == nil {
			t.Fatalf("host termination was masked: %+v", response)
		}
	}
}

func TestWorkerFailureNeverAcceptsSuccessfulOrMalformedOutput(t *testing.T) {
	for _, stdout := range []string{`{"status":"completed","error":"pretend success"}`, `{"status":"failed","error":"x"} {}`, `not json`} {
		response := workerFailure("bounded optimizer", host.Output{ExitCode: 2, Stdout: []byte(stdout)}, errors.New("exit status 2"))
		if response.Status != "failed" || response.Error == "pretend success" || response.Error == "x" {
			t.Fatalf("invalid failure channel was accepted: %+v", response)
		}
	}
}

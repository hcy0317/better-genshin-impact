//go:build !windows

package host

import (
	"errors"
	"os/exec"
)

type workerJob struct{}

func newWorkerJob(uint64) (*workerJob, error) {
	return nil, errors.New("bounded worker execution currently requires Windows")
}
func (*workerJob) Assign(int) error    { return errors.New("unsupported platform") }
func (*workerJob) Close()              {}
func (*workerJob) Terminate() error    { return errors.New("unsupported platform") }
func (*workerJob) PeakMemory() *uint64 { return nil }
func configureProcess(*exec.Cmd)       {}

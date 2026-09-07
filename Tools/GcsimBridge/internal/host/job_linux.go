//go:build linux

package host

import (
	"errors"
	"fmt"
	"golang.org/x/sys/unix"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
)

type workerJob struct {
	fd     int
	memory uint64
}

func newWorkerJob(memory uint64) (*workerJob, error) { return &workerJob{fd: -1, memory: memory}, nil }
func (*workerJob) Isolation() string                 { return "linux_pidfd_rlimit_data_as" }
func (j *workerJob) Assign(pid int) error {
	fd, err := unix.PidfdOpen(pid, 0)
	if err != nil {
		return fmt.Errorf("pidfd is required: %w", err)
	}
	j.fd = fd
	status, err := os.ReadFile(fmt.Sprintf("/proc/%d/status", pid))
	if err != nil {
		return err
	}
	values := map[string]uint64{}
	for _, line := range strings.Split(string(status), "\n") {
		f := strings.Fields(line)
		if len(f) >= 2 {
			n, e := strconv.ParseUint(f[1], 10, 64)
			if e == nil {
				values[strings.TrimSuffix(f[0], ":")] = n * 1024
			}
		}
	}
	maps, err := os.ReadFile(fmt.Sprintf("/proc/%d/maps", pid))
	if err != nil {
		return err
	}
	fileMappings := uint64(0)
	for _, line := range strings.Split(string(maps), "\n") {
		fields := strings.Fields(line)
		if len(fields) < 6 || !strings.HasPrefix(fields[5], "/") {
			continue
		}
		bounds := strings.Split(fields[0], "-")
		if len(bounds) != 2 {
			return errors.New("invalid worker mapping")
		}
		start, e1 := strconv.ParseUint(bounds[0], 16, 64)
		end, e2 := strconv.ParseUint(bounds[1], 16, 64)
		if e1 != nil || e2 != nil || end < start {
			return errors.New("invalid worker address range")
		}
		fileMappings += end - start
	}
	// RLIMIT_DATA covers Go's anonymous heap mappings (Linux >=4.7). Reserve
	// all existing file mappings and 16 MiB for bounded runtime/thread overhead;
	// RLIMIT_AS also stops further unbounded virtual mappings. No RSS polling.
	reserve := fileMappings + 16<<20
	if j.memory <= reserve || values["VmData"] > j.memory-reserve {
		return errors.New("memory budget is below the trusted worker runtime footprint")
	}
	limits := map[int]uint64{unix.RLIMIT_DATA: j.memory - reserve, unix.RLIMIT_AS: values["VmSize"] + j.memory - reserve, unix.RLIMIT_CORE: 0, unix.RLIMIT_STACK: 1 << 20}
	for resource, value := range limits {
		limit := unix.Rlimit{Cur: value, Max: value}
		if err := unix.Prlimit(pid, resource, &limit, nil); err != nil {
			return err
		}
		var actual unix.Rlimit
		if err := unix.Prlimit(pid, resource, nil, &actual); err != nil {
			return err
		}
		if actual != limit {
			return errors.New("worker resource limits could not be verified")
		}
	}
	return unix.Setpriority(unix.PRIO_PROCESS, pid, 10)
}
func (j *workerJob) Terminate() error {
	if j.fd < 0 {
		return nil
	}
	err := unix.PidfdSendSignal(j.fd, unix.SIGKILL, nil, 0)
	if errors.Is(err, unix.ESRCH) {
		return nil
	}
	return err
}
func (j *workerJob) Close() {
	if j.fd >= 0 {
		_ = j.Terminate()
		_ = unix.Close(j.fd)
		j.fd = -1
	}
}
func (*workerJob) PeakMemory() *uint64 { return nil }
func configureProcess(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Pdeathsig: syscall.SIGKILL, Setpgid: true}
}

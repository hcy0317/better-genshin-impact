//go:build windows

package host

import (
	"fmt"
	"os/exec"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

type workerJob struct{ handle windows.Handle }

func newWorkerJob(memoryBytes uint64) (*workerJob, error) {
	handle, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return nil, err
	}
	job := &workerJob{handle: handle}
	var info windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	flags := uint32(windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | windows.JOB_OBJECT_LIMIT_PROCESS_MEMORY |
		windows.JOB_OBJECT_LIMIT_ACTIVE_PROCESS | windows.JOB_OBJECT_LIMIT_PRIORITY_CLASS)
	info.BasicLimitInformation.LimitFlags = flags
	info.BasicLimitInformation.ActiveProcessLimit = 1
	info.BasicLimitInformation.PriorityClass = windows.BELOW_NORMAL_PRIORITY_CLASS
	info.ProcessMemoryLimit = uintptr(memoryBytes)
	_, err = windows.SetInformationJobObject(handle, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&info)), uint32(unsafe.Sizeof(info)))
	if err != nil {
		job.Close()
		return nil, err
	}
	info = windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{}
	err = windows.QueryInformationJobObject(handle, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&info)), uint32(unsafe.Sizeof(info)), nil)
	if err != nil || info.ProcessMemoryLimit != uintptr(memoryBytes) || info.BasicLimitInformation.LimitFlags&flags != flags {
		job.Close()
		return nil, fmt.Errorf("worker limits could not be verified: %v", err)
	}
	return job, nil
}

func (job *workerJob) Assign(pid int) error {
	process, err := windows.OpenProcess(windows.PROCESS_SET_QUOTA|windows.PROCESS_TERMINATE, false, uint32(pid))
	if err != nil {
		return err
	}
	defer windows.CloseHandle(process)
	return windows.AssignProcessToJobObject(job.handle, process)
}

func (job *workerJob) Close()           { _ = windows.CloseHandle(job.handle) }
func (job *workerJob) Terminate() error { return windows.TerminateJobObject(job.handle, 1) }
func (job *workerJob) PeakMemory() *uint64 {
	var info windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	if windows.QueryInformationJobObject(job.handle, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&info)), uint32(unsafe.Sizeof(info)), nil) != nil {
		return nil
	}
	peak := uint64(info.PeakProcessMemoryUsed)
	return &peak
}

func configureProcess(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: windows.CREATE_NO_WINDOW}
}

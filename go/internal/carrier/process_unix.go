//go:build unix

package carrier

import (
	"os"
	"os/exec"
	"syscall"
)

// isolate starts the worker in its own process group so that expiry can stop
// descendants that inherited its pipes, such as a launcher that does not exec.
func isolate(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true} }

func killTree(process *os.Process) {
	_ = syscall.Kill(-process.Pid, syscall.SIGKILL)
	_ = process.Kill()
}

func signalName(state *os.ProcessState) string {
	status, ok := state.Sys().(syscall.WaitStatus)
	if !ok || !status.Signaled() {
		return ""
	}
	names := map[syscall.Signal]string{
		syscall.SIGKILL: "SIGKILL", syscall.SIGTERM: "SIGTERM", syscall.SIGINT: "SIGINT", syscall.SIGHUP: "SIGHUP",
		syscall.SIGPIPE: "SIGPIPE", syscall.SIGSEGV: "SIGSEGV", syscall.SIGABRT: "SIGABRT", syscall.SIGBUS: "SIGBUS",
		syscall.SIGILL: "SIGILL", syscall.SIGFPE: "SIGFPE", syscall.SIGQUIT: "SIGQUIT", syscall.SIGTRAP: "SIGTRAP",
	}
	if name, ok := names[status.Signal()]; ok {
		return name
	}
	return status.Signal().String()
}

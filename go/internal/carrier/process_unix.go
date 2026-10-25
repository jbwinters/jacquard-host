//go:build unix

package carrier

import (
	"fmt"
	"os"
	"os/exec"
	"syscall"
)

// isolate starts the worker in its own process group so that expiry can stop
// descendants that inherited its pipes, such as a launcher that does not exec.
func isolate(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true} }

// killTree stops a worker and its process group. The caller guarantees the
// leader is unreaped, which keeps the group id from being reused.
func killTree(process *os.Process) {
	_ = syscall.Kill(-process.Pid, syscall.SIGKILL)
	_ = process.Kill()
}

// killGroup stops the members left in an exited worker's process group. The
// caller guarantees the leader has not been reaped (see waitExited).
func killGroup(pgid int) { _ = syscall.Kill(-pgid, syscall.SIGKILL) }

func signalName(state *os.ProcessState) string {
	status, ok := state.Sys().(syscall.WaitStatus)
	if !ok || !status.Signaled() {
		return ""
	}
	if name, ok := signalNames[status.Signal()]; ok {
		return name
	}
	return fmt.Sprintf("SIG%d", int(status.Signal()))
}

// signalNames covers the POSIX signals common to Linux and macOS.
var signalNames = map[syscall.Signal]string{
	syscall.SIGABRT: "SIGABRT", syscall.SIGALRM: "SIGALRM", syscall.SIGBUS: "SIGBUS", syscall.SIGCHLD: "SIGCHLD",
	syscall.SIGCONT: "SIGCONT", syscall.SIGFPE: "SIGFPE", syscall.SIGHUP: "SIGHUP", syscall.SIGILL: "SIGILL",
	syscall.SIGINT: "SIGINT", syscall.SIGIO: "SIGIO", syscall.SIGKILL: "SIGKILL", syscall.SIGPIPE: "SIGPIPE",
	syscall.SIGPROF: "SIGPROF", syscall.SIGQUIT: "SIGQUIT", syscall.SIGSEGV: "SIGSEGV", syscall.SIGSTOP: "SIGSTOP",
	syscall.SIGSYS: "SIGSYS", syscall.SIGTERM: "SIGTERM", syscall.SIGTRAP: "SIGTRAP", syscall.SIGTSTP: "SIGTSTP",
	syscall.SIGTTIN: "SIGTTIN", syscall.SIGTTOU: "SIGTTOU", syscall.SIGURG: "SIGURG", syscall.SIGUSR1: "SIGUSR1",
	syscall.SIGUSR2: "SIGUSR2", syscall.SIGVTALRM: "SIGVTALRM", syscall.SIGWINCH: "SIGWINCH", syscall.SIGXCPU: "SIGXCPU",
	syscall.SIGXFSZ: "SIGXFSZ",
}

//go:build unix

package standin

import (
	"os"
	"os/exec"
	"syscall"
)

func startEscaped() (*exec.Cmd, error) {
	child := exec.Command("sleep", "30")
	child.Stderr = os.Stderr
	child.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	return child, child.Start()
}

//go:build !unix

package carrier

import (
	"os"
	"os/exec"
)

func isolate(*exec.Cmd) {}

func killTree(process *os.Process) { _ = process.Kill() }

func signalName(*os.ProcessState) string { return "" }

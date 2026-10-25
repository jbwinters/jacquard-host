//go:build !unix

package standin

import (
	"errors"
	"os/exec"
)

func startEscaped() (*exec.Cmd, error) { return nil, errors.New("not supported") }

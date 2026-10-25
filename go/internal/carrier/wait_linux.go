//go:build linux

package carrier

import (
	"syscall"
	"unsafe"
)

// waitExited blocks until the child exits without reaping it (waitid with
// WEXITED|WNOWAIT), so its pid and process group id stay reserved.
func waitExited(pid int) bool {
	const pPID, wExited, wNoWait = 1, 4, 0x01000000
	var info [128]byte // siginfo_t
	for {
		_, _, errno := syscall.Syscall6(syscall.SYS_WAITID, pPID, uintptr(pid), uintptr(unsafe.Pointer(&info[0])), wExited|wNoWait, 0, 0)
		switch errno {
		case 0:
			return true
		case syscall.EINTR:
			continue
		default:
			return false
		}
	}
}

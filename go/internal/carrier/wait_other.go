//go:build !linux

package carrier

// waitExited reports that this platform cannot wait without reaping; the
// host then skips the post-exit group kill and only stops draining stderr.
func waitExited(int) bool { return false }

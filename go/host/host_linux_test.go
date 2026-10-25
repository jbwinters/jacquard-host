//go:build linux

package host_test

import (
	"context"
	"os"
	"testing"
	"time"
)

// Post-exit group cleanup needs waitid(WNOWAIT), so it is Linux-only.
func TestDescendantHoldingStderrAfterExitFailsClosed(t *testing.T) {
	for mode, status := range map[string]int{"stderr-descendant": 0, "stderr-descendant-nonzero": 74} {
		result := faulty(t, mode, 5*time.Second, nil).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
		if result.OK() || result.Kind != "carrier_loss" || result.Code() != "E1611" {
			t.Fatalf("%s: kind %s: %s", mode, result.Kind, result.Message())
		}
		if result.ExitStatus == nil || *result.ExitStatus != status {
			t.Fatalf("%s: exit status %v", mode, result.ExitStatus)
		}
		descendantDies(t, mode, result.Stderr)
	}
}

func openDescriptors(t *testing.T) int {
	t.Helper()
	entries, err := os.ReadDir("/proc/self/fd")
	if err != nil {
		t.Skip("no /proc/self/fd")
	}
	return len(entries)
}

func TestCompletedInvocationsReleaseTheirPipes(t *testing.T) {
	h := faulty(t, "ok", 5*time.Second, nil)
	ids := identities(t)
	h.Invoke(context.Background(), ids["kit-ready"], unit, nil, nil) // warm up lazily opened files
	before := openDescriptors(t)
	for range 5 {
		if result := h.Invoke(context.Background(), ids["kit-ready"], unit, nil, nil); !result.OK() {
			t.Fatal(result.Message())
		}
	}
	if after := openDescriptors(t); after > before {
		t.Fatalf("open descriptors grew from %d to %d", before, after)
	}
}

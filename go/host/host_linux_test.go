//go:build linux

package host_test

import (
	"context"
	"os"
	"testing"
	"time"
)

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

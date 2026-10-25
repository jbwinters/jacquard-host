//go:build unix

package host_test

import (
	"context"
	"strconv"
	"syscall"
	"testing"
	"time"
)

// descendantDies waits for the process named by a stand-in's stderr to exit.
func descendantDies(t *testing.T, stderr []byte) {
	t.Helper()
	pid, err := strconv.Atoi(string(stderr))
	if err != nil {
		t.Fatalf("stand-in stderr %q", stderr)
	}
	deadline := time.Now().Add(2 * time.Second)
	for syscall.Kill(pid, 0) == nil {
		if time.Now().After(deadline) {
			_ = syscall.Kill(pid, syscall.SIGKILL)
			t.Fatal("descendant survived the worker's expiry")
		}
		time.Sleep(10 * time.Millisecond)
	}
}

func TestDeadlineStopsDescendantsHoldingThePipes(t *testing.T) {
	start := time.Now()
	result := faulty(t, "descendant", 500*time.Millisecond, nil).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	if elapsed := time.Since(start); elapsed > 3*time.Second || result.HostEvidence["reason"] != "deadline" {
		t.Fatalf("returned after %v with %v", elapsed, result.HostEvidence)
	}
	descendantDies(t, result.Stderr)
}

func TestDescendantHoldingStderrAfterExitFailsClosed(t *testing.T) {
	result := faulty(t, "stderr-descendant", 5*time.Second, nil).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	if result.OK() || result.Kind != "carrier_loss" || result.Code() != "E1611" {
		t.Fatalf("kind %s: %s", result.Kind, result.Message())
	}
	descendantDies(t, result.Stderr)
}

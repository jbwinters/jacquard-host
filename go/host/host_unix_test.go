//go:build unix

package host_test

import (
	"context"
	"runtime"
	"strconv"
	"syscall"
	"testing"
	"time"
)

func descendantPID(t *testing.T, mode string, stderr []byte) int {
	t.Helper()
	pid, err := strconv.Atoi(string(stderr))
	if err != nil {
		t.Fatalf("%s: stand-in stderr %q", mode, stderr)
	}
	return pid
}

// descendantDies waits for the process named by a stand-in's stderr to exit.
func descendantDies(t *testing.T, mode string, stderr []byte) {
	t.Helper()
	pid := descendantPID(t, mode, stderr)
	deadline := time.Now().Add(2 * time.Second)
	for syscall.Kill(pid, 0) == nil {
		if time.Now().After(deadline) {
			_ = syscall.Kill(pid, syscall.SIGKILL)
			t.Fatalf("%s: descendant survived the worker", mode)
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
	descendantDies(t, "descendant", result.Stderr)
}

func TestDeadlineDuringTheExitGraceIsReportedAsTheDeadline(t *testing.T) {
	result := faulty(t, "stderr-descendant", 500*time.Millisecond, nil).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	if result.HostEvidence["reason"] != "deadline" {
		t.Fatalf("reason %v: %s", result.HostEvidence["reason"], result.Message())
	}
	if runtime.GOOS == "linux" { // elsewhere the exited worker may already be reaped
		descendantDies(t, "stderr-descendant", result.Stderr)
	}
}

func TestSlowCleanExitSucceeds(t *testing.T) {
	start := time.Now()
	result := faulty(t, "slow-exit", 5*time.Second, nil).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	if !result.OK() || time.Since(start) < 1500*time.Millisecond {
		t.Fatalf("slow clean exit: %s after %v", result.Message(), time.Since(start))
	}
}

func TestEscapedDescendantDoesNotHoldTheHost(t *testing.T) {
	start := time.Now()
	result := faulty(t, "escaped-descendant", 10*time.Second, nil).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	pid := descendantPID(t, "escaped-descendant", result.Stderr)
	defer syscall.Kill(pid, syscall.SIGKILL)
	if result.OK() || result.Kind != "carrier_loss" || time.Since(start) > 5*time.Second {
		t.Fatalf("escaped descendant: kind %s after %v", result.Kind, time.Since(start))
	}
}

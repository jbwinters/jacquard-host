package host_test

import (
	"context"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/internal/standin"
	"github.com/jbwinters/jacquard-host/go/kit"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

func TestMain(m *testing.M) {
	standin.MainIfRequested()
	os.Exit(m.Run())
}

var unit = protocol.Interface{Result: protocol.TupleType{}}

func identities(t *testing.T) map[string]protocol.Hash {
	t.Helper()
	loaded, err := kit.Load()
	if err != nil {
		t.Fatal(err)
	}
	return loaded.Identities
}

func faulty(t *testing.T, mode string, deadline time.Duration, limits *protocol.Limits) *host.Host {
	t.Helper()
	h, err := host.New("unused", host.Options{Command: standin.Command(), Env: standin.Env(mode, ""), Deadline: deadline, Limits: limits})
	if err != nil {
		t.Fatal(err)
	}
	return h
}

func TestMalformedExtraAndNoncanonicalCoreFramesFailClosed(t *testing.T) {
	ids := identities(t)
	for mode, expected := range map[string]string{"unknown-field": "E1601", "invalid-hash": "E1601", "extra": "E1608"} {
		result := faulty(t, mode, 2*time.Second, nil).Invoke(context.Background(), ids["kit-ready"], unit, nil, nil)
		if result.Kind != host.KindProtocolError || result.Code() != expected {
			t.Errorf("%s: kind %s code %s (%s)", mode, result.Kind, result.Code(), result.Message())
		}
	}
}

func TestCarrierFailuresRetainHostEvidence(t *testing.T) {
	ids := identities(t)
	for _, mode := range []string{"exit", "truncated", "silent", "stall", "nonzero"} {
		result := faulty(t, mode, time.Second, nil).Invoke(context.Background(), ids["kit-ready"], unit, nil, nil)
		if result.Kind != host.KindCarrierLoss || result.Code() != "E1611" || result.HostEvidence["schema"] != host.EvidenceSchema {
			t.Errorf("%s: kind %s code %s evidence %v", mode, result.Kind, result.Code(), result.HostEvidence)
		}
		sawOutcome := false
		for _, frame := range result.CoreFrames {
			sawOutcome = sawOutcome || frame["kind"] == "outcome"
		}
		if (mode == "stall" || mode == "nonzero") != sawOutcome {
			t.Errorf("%s: outcome observed = %v", mode, sawOutcome)
		}
		expired := mode == "silent" || mode == "stall"
		if expired != (result.HostEvidence["reason"] == "deadline") {
			t.Errorf("%s: reason %v", mode, result.HostEvidence["reason"])
		}
		_, err := result.Unwrap()
		if expected := map[bool]error{true: host.ErrDeadlineExceeded, false: host.ErrWorkerLost}[expired]; !errors.Is(err, expected) {
			t.Errorf("%s: unwrap %v", mode, err)
		}
	}
}

func TestStderrIsDrainedAndRetentionObeysTheSelectedCeiling(t *testing.T) {
	limits := protocol.HardLimits
	limits.MaxStderrBytes = 17
	result := faulty(t, "stderr", 5*time.Second, &limits).Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	if !result.OK() || len(result.Stderr) != 17 {
		t.Fatalf("%s, stderr %d bytes", result.Message(), len(result.Stderr))
	}
}

func TestBackpressureSpendsTheSameLifetime(t *testing.T) {
	ids := identities(t)
	spec := protocol.Interface{Parameters: []protocol.BoundaryType{protocol.Nominal{Identity: ids["text"]}}, Result: protocol.Nominal{Identity: ids["text"]}}
	start := time.Now()
	result := faulty(t, "no-read", time.Second, nil).Invoke(context.Background(), ids["kit-ready"], spec, []protocol.Value{protocol.Text(strings.Repeat("x", 220000))}, nil)
	if result.HostEvidence["reason"] != "deadline" || time.Since(start) > 4*time.Second {
		t.Fatalf("reason %v after %v", result.HostEvidence["reason"], time.Since(start))
	}
	_, err := result.Unwrap()
	if !errors.Is(err, host.ErrDeadlineExceeded) {
		t.Fatalf("unwrap %v", err)
	}
}

func TestCancellationAbortsWithoutRetry(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	result := faulty(t, "silent", 5*time.Second, nil).Invoke(ctx, identities(t)["kit-ready"], unit, nil, nil)
	if result.HostEvidence["reason"] != "aborted" {
		t.Fatalf("reason %v", result.HostEvidence["reason"])
	}
}

func TestLocalRefusalStartsNoWorkerAndInvalidOptionsAreRejected(t *testing.T) {
	limits := protocol.HardLimits
	limits.MaxFrameBytes = 20
	h, err := host.New("unused", host.Options{Command: []string{"/no/such/executable"}, Limits: &limits})
	if err != nil {
		t.Fatal(err)
	}
	result := h.Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil)
	if result.Kind != host.KindHostError || result.Code() != "E1602" || result.ExitStatus != nil {
		t.Fatalf("kind %s code %s", result.Kind, result.Code())
	}
	spawn, _ := host.New("unused", host.Options{Command: []string{"/no/such/executable"}})
	if reason := spawn.Invoke(context.Background(), identities(t)["kit-ready"], unit, nil, nil).HostEvidence["reason"]; reason != "worker_spawn_failed" {
		t.Fatalf("spawn reason %v", reason)
	}
	zero := protocol.HardLimits
	zero.MaxArguments = 0
	for _, options := range []host.Options{{Deadline: -time.Second}, {Command: []string{""}}, {Limits: &zero}} {
		if _, err := host.New("unused", options); err == nil {
			t.Fatalf("accepted %+v", options)
		}
	}
}

func TestRegistryRefusesDuplicatesAndBadIdentities(t *testing.T) {
	registry := host.NewRegistry()
	a, b := protocol.Hash(strings.Repeat("a", 64)), protocol.Hash(strings.Repeat("b", 64))
	handler := func(context.Context, []protocol.Value) (protocol.Value, error) { return protocol.Tuple{}, nil }
	if err := registry.Bind(b, a, handler); err != nil {
		t.Fatal(err)
	}
	if err := registry.Bind(a, b, handler); err != nil {
		t.Fatal(err)
	}
	if registry.Bind(a, b, handler) == nil || registry.Bind("A", b, handler) == nil || registry.Bind(a, a, nil) == nil {
		t.Fatal("registry accepted an invalid binding")
	}
	if entries := registry.Entries(); entries[0].Effect != a || entries[1].Effect != b {
		t.Fatalf("entries not sorted: %v", entries)
	}
	if _, err := host.NewFailure(protocol.OutsideFailure, protocol.Unknown, ""); err == nil {
		t.Fatal("invalid failure pair accepted")
	}
}

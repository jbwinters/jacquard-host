package host_test

import (
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/kit"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

type live struct {
	options host.Options
	store   string
	kit     *kit.Kit
}

// prepareLive installs the kit fixtures into a fresh store with the real Core
// named by JACQUARD_BIN and JACQUARD_PRELUDE, or skips without them.
func prepareLive(t *testing.T) live {
	t.Helper()
	binary, prelude := os.Getenv("JACQUARD_BIN"), os.Getenv("JACQUARD_PRELUDE")
	if binary == "" || prelude == "" {
		t.Skip("live tests need JACQUARD_BIN and JACQUARD_PRELUDE")
	}
	loaded, err := kit.Load()
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()
	fixtures, _ := kit.File("kit/fixtures.jac")
	source := filepath.Join(root, "fixtures.jac")
	if err := os.WriteFile(source, fixtures, 0o600); err != nil {
		t.Fatal(err)
	}
	env := append(os.Environ(), "JACQUARD_PRELUDE="+prelude)
	store := filepath.Join(root, "store")
	command := exec.Command(binary, "run", source, "--store", store)
	command.Env = env
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("installing fixtures: %v\n%s", err, output)
	}
	return live{host.Options{Command: []string{binary}, Env: env}, store, loaded}
}

func (l live) host(t *testing.T, deadline time.Duration) *host.Host {
	options := l.options
	options.Deadline = deadline
	h, err := host.New(l.store, options)
	if err != nil {
		t.Fatal(err)
	}
	return h
}

func (l live) echo() protocol.Interface {
	text := protocol.Nominal{Identity: l.kit.Identities["text"]}
	return protocol.Interface{Parameters: []protocol.BoundaryType{text}, Result: text, Effects: []protocol.Hash{l.kit.Identities["kit-world"]}}
}

func (l live) registry(t *testing.T, handler host.Handler) *host.OperationRegistry {
	registry := host.NewRegistry()
	if err := registry.Bind(l.kit.Identities["kit-world"], l.kit.Identities["send"], handler); err != nil {
		t.Fatal(err)
	}
	return registry
}

func TestLiveCoreMatchesAllRecordings(t *testing.T) {
	l := prepareLive(t)
	replays, err := kit.ReplayKit(context.Background(), l.store, kit.Options{Command: l.options.Command, Env: l.options.Env}, l.kit)
	if err != nil {
		t.Fatal(err)
	}
	for _, replay := range replays {
		if len(replay.Differences) != 0 || len(replay.ProseDrift) != 0 {
			t.Errorf("%s: %v, prose drift %v", replay.Name, replay.Differences, replay.ProseDrift)
		}
	}
	if len(replays) != kit.TranscriptCount {
		t.Fatalf("%d replays", len(replays))
	}
}

func TestLivePureAndEffectfulInvocationsRetainEvidence(t *testing.T) {
	l := prepareLive(t)
	h := l.host(t, 0)
	pure := h.Invoke(context.Background(), l.kit.Identities["kit-ready"], unit, nil, nil)
	if value, err := pure.Unwrap(); err != nil || len(value.(protocol.Tuple)) != 0 || *pure.ExitStatus != 0 {
		t.Fatalf("pure: %v %v", value, err)
	}
	var calls atomic.Int32
	registry := l.registry(t, func(_ context.Context, args []protocol.Value) (protocol.Value, error) {
		calls.Add(1)
		return args[0].(protocol.Text) + "!", nil
	})
	result := h.Invoke(context.Background(), l.kit.Identities["kit-echo"], l.echo(), []protocol.Value{protocol.Text("ping")}, registry)
	value, err := result.Unwrap()
	if err != nil || value != protocol.Text("ping!") || calls.Load() != 1 || len(result.Requests) != 1 ||
		result.HostFrames[2]["kind"] != "effect_ok" || result.Evidence["core"].(protocol.Object)["terminal"] != "ok" {
		t.Fatalf("echo: %v %v calls=%d", value, err, calls.Load())
	}
	if probe := h.ShutdownProbe(context.Background()); probe.Kind != host.KindShutdownAck {
		t.Fatalf("shutdown: %s", probe.Message())
	}
}

func TestLiveFailurePairsAreNeverRetried(t *testing.T) {
	l := prepareLive(t)
	cases := []struct {
		failure *host.Failure
		code    string
	}{{host.Unsupported("synthetic failure"), "E1606"}, {host.Refused("synthetic failure"), "E1607"},
		{host.OutsideFailed("synthetic failure"), "E1613"}, {host.CompletionUnknown("synthetic failure"), "E1612"}}
	for _, c := range cases {
		var calls atomic.Int32
		registry := l.registry(t, func(context.Context, []protocol.Value) (protocol.Value, error) {
			calls.Add(1)
			return nil, c.failure
		})
		result := l.host(t, 0).Invoke(context.Background(), l.kit.Identities["kit-echo"], l.echo(), []protocol.Value{protocol.Text("ping")}, registry)
		if result.Code() != c.code || calls.Load() != 1 || !strings.HasSuffix(result.Diagnostics[0].Cause, "synthetic failure") {
			t.Errorf("%s: code %s calls %d", c.code, result.Code(), calls.Load())
		}
	}
	for name, handler := range map[string]host.Handler{
		"error": func(context.Context, []protocol.Value) (protocol.Value, error) {
			return nil, errors.New("private detail")
		},
		"panic": func(context.Context, []protocol.Value) (protocol.Value, error) { panic("private detail") },
	} {
		result := l.host(t, 0).Invoke(context.Background(), l.kit.Identities["kit-echo"], l.echo(), []protocol.Value{protocol.Text("ping")}, l.registry(t, handler))
		if result.Code() != "E1612" || strings.Contains(result.Message(), "private detail") {
			t.Errorf("%s: %s", name, result.Message())
		}
		if _, err := result.Unwrap(); !errors.Is(err, host.ErrOperationFailed) {
			t.Errorf("%s: unwrap %v", name, err)
		}
	}
}

func TestLiveHandlerDeadlineKillsTheWorkerWithoutALateResponse(t *testing.T) {
	l := prepareLive(t)
	for name, handler := range map[string]host.Handler{
		// Cooperative: waits for cancellation.
		"cooperative": func(ctx context.Context, _ []protocol.Value) (protocol.Value, error) {
			<-ctx.Done()
			return protocol.Text("late"), nil
		},
		// Ignores its context; the host abandons it.
		"blocking": func(context.Context, []protocol.Value) (protocol.Value, error) {
			time.Sleep(time.Second)
			return protocol.Text("late"), nil
		},
	} {
		var calls atomic.Int32
		var cancelled atomic.Bool
		registry := l.registry(t, func(ctx context.Context, args []protocol.Value) (protocol.Value, error) {
			calls.Add(1)
			value, err := handler(ctx, args)
			cancelled.Store(ctx.Err() != nil)
			return value, err
		})
		start := time.Now()
		result := l.host(t, 250*time.Millisecond).Invoke(context.Background(), l.kit.Identities["kit-echo"], l.echo(), []protocol.Value{protocol.Text("ping")}, registry)
		if result.HostEvidence["reason"] != "deadline" || result.Code() != "E1611" || calls.Load() != 1 ||
			len(result.HostFrames) != 2 || time.Since(start) > 2*time.Second {
			t.Errorf("%s: %v frames=%d after %v", name, result.HostEvidence, len(result.HostFrames), time.Since(start))
		}
		if name == "cooperative" && !cancelled.Load() {
			t.Errorf("%s: handler context was not cancelled", name)
		}
	}
}

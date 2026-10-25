package kit_test

import (
	"context"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jbwinters/jacquard-host/go/internal/standin"
	"github.com/jbwinters/jacquard-host/go/kit"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

func TestMain(m *testing.M) {
	standin.MainIfRequested()
	os.Exit(m.Run())
}

func load(t *testing.T) *kit.Kit {
	t.Helper()
	loaded, err := kit.Load()
	if err != nil {
		t.Fatal(err)
	}
	return loaded
}

func find(t *testing.T, loaded *kit.Kit, name string) kit.Transcript {
	for _, transcript := range loaded.Transcripts {
		if transcript.Name == name {
			return transcript
		}
	}
	t.Fatalf("no transcript %s", name)
	return kit.Transcript{}
}

func code(t *testing.T, err error, expected string) {
	t.Helper()
	var protocolError *protocol.ProtocolError
	if !errors.As(err, &protocolError) || protocolError.Code != expected {
		t.Fatalf("expected %s, got %v", expected, err)
	}
}

func TestPinnedKitLoads(t *testing.T) {
	loaded := load(t)
	if len(loaded.Transcripts) != kit.TranscriptCount || loaded.Identities["kit-echo"] == "" {
		t.Fatalf("kit shape: %d transcripts", len(loaded.Transcripts))
	}
}

func TestSessionEnforcesOneInvocationAndLocalTerminalRefusal(t *testing.T) {
	loaded := load(t)
	pure := loaded.Transcripts[0]
	session := protocol.NewSession()
	if _, err := session.Receive(pure.CoreOutput[0]); err != nil {
		t.Fatal(err)
	}
	if _, err := session.Select(protocol.HardLimits); err != nil {
		t.Fatal(err)
	}
	unit := protocol.Interface{Result: protocol.TupleType{}}
	if _, err := session.Invoke(loaded.Identities["kit-ready"], unit, nil, nil); err != nil {
		t.Fatal(err)
	}
	_, err := session.Invoke(protocol.Hash(strings.Repeat("a", 64)), unit, nil, nil)
	code(t, err, "E1608")
	if _, err := session.Receive(pure.CoreOutput[1]); err != nil {
		t.Fatal(err)
	}
	_, err = session.Shutdown()
	code(t, err, "E1608")
	_, err = session.Receive(pure.CoreOutput[1])
	code(t, err, "E1608")
}

func TestRequestOrderingAndOneResponseSlot(t *testing.T) {
	loaded := load(t)
	transcript := find(t, loaded, "one-once-effect-success")
	session := protocol.NewSession()
	if _, err := session.Receive(transcript.CoreOutput[0]); err != nil {
		t.Fatal(err)
	}
	if _, err := session.Select(protocol.HardLimits); err != nil {
		t.Fatal(err)
	}
	var invoke protocol.Object
	for _, step := range transcript.HostInput {
		if step.Frame["kind"] == "invoke" {
			invoke = step.Frame
		}
	}
	iface := invoke["interface"].(protocol.Object)
	spec := protocol.Interface{}
	for _, parameter := range iface["parameters"].([]any) {
		decoded, err := protocol.DecodeType(parameter, protocol.NewBudget(protocol.HardLimits))
		if err != nil {
			t.Fatal(err)
		}
		spec.Parameters = append(spec.Parameters, decoded)
	}
	spec.Result, _ = protocol.DecodeType(iface["result"], protocol.NewBudget(protocol.HardLimits))
	for _, effect := range iface["effects"].([]any) {
		spec.Effects = append(spec.Effects, protocol.Hash(effect.(string)))
	}
	var operations []protocol.OperationEntry
	for _, entry := range invoke["capabilities"].(protocol.Object)["operations"].([]any) {
		e := entry.(protocol.Object)
		operations = append(operations, protocol.OperationEntry{Effect: protocol.Hash(e["effect"].(string)), Operation: protocol.Hash(e["operation"].(string))})
	}
	callable := protocol.Hash(invoke["target"].(protocol.Object)["callable"].(string))
	if _, err := session.Invoke(callable, spec, []protocol.Value{protocol.Text("ping")}, operations); err != nil {
		t.Fatal(err)
	}
	request := transcript.CoreOutput[1]
	with := func(key string, value any) protocol.Object {
		out := protocol.Object{}
		for k, v := range request {
			out[k] = v
		}
		out[key] = value
		return out
	}
	_, err := session.Receive(with("invocation_id", int64(0)))
	code(t, err, "E1601")
	_, err = session.Receive(with("invocation_id", "ffffffffffffffff"))
	code(t, err, "E1608")
	_, err = session.Receive(with("request_id", "0000000000000002"))
	code(t, err, "E1608")
	if _, err := session.Receive(request); err != nil {
		t.Fatal(err)
	}
	_, err = session.Receive(request)
	code(t, err, "E1608")
	_, err = session.RespondFailure(protocol.OutsideFailure, protocol.Unknown, "")
	code(t, err, "E1601")
	if _, err := session.RespondOK(protocol.Text("pong")); err != nil {
		t.Fatal(err)
	}
	_, err = session.RespondOK(protocol.Text("again"))
	code(t, err, "E1608")
}

func standinOptions(name string) kit.Options {
	return kit.Options{Command: standin.Command(), Env: standin.Env("replay", name), Deadline: 10 * time.Second}
}

func TestAllRecordingsReplayThroughTheGoCarrier(t *testing.T) {
	loaded := load(t)
	for _, transcript := range loaded.Transcripts {
		replay := kit.ReplayOne(context.Background(), transcript, "unused", standinOptions(transcript.Name))
		if len(replay.Differences) != 0 || len(replay.ProseDrift) != 0 || replay.Status != transcript.Status {
			t.Errorf("%s: differences %v, prose drift %v, status %s", transcript.Name, replay.Differences, replay.ProseDrift, replay.Status)
		}
	}
}

func TestProseDriftIsReportedWithoutNormalizing(t *testing.T) {
	loaded := load(t)
	original := find(t, loaded, "refused-authority")
	modified := original
	modified.CoreOutput = append([]protocol.Object(nil), original.CoreOutput...)
	last := len(modified.CoreOutput) - 1
	frame, err := protocol.Decode([]byte(mustStringify(t, original.CoreOutput[last])), 256)
	if err != nil {
		t.Fatal(err)
	}
	diagnostic := frame.(protocol.Object)["result"].(protocol.Object)["diagnostics"].([]any)[0].(protocol.Object)
	diagnostic["summary"] = diagnostic["summary"].(string) + " changed"
	modified.CoreOutput[last] = frame.(protocol.Object)
	replay := kit.ReplayOne(context.Background(), modified, "unused", standinOptions(original.Name))
	if len(replay.Differences) != 0 || len(replay.ProseDrift) != 1 {
		t.Fatalf("differences %v, prose drift %v", replay.Differences, replay.ProseDrift)
	}
	observed := replay.CoreFrames[len(replay.CoreFrames)-1]["result"].(protocol.Object)["diagnostics"].([]any)[0].(protocol.Object)
	recorded := original.CoreOutput[last]["result"].(protocol.Object)["diagnostics"].([]any)[0].(protocol.Object)
	if observed["summary"] != recorded["summary"] {
		t.Fatal("observed Core prose was altered")
	}
}

func mustStringify(t *testing.T, value any) string {
	text, err := protocol.Stringify(value, 256)
	if err != nil {
		t.Fatal(err)
	}
	return text
}

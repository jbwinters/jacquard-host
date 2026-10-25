// Package kit embeds the pinned host-protocol-v0 conformance kit and replays
// its recordings, including the intentional hostile host inputs, against a
// worker through the independent Go carrier.
package kit

import (
	"context"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"github.com/jbwinters/jacquard-host/go/internal/carrier"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

//go:embed data/host-protocol-v0
var files embed.FS

const root = "data/host-protocol-v0/"

// TranscriptCount is the number of recordings in the pinned kit.
const TranscriptCount = 21

// Step is one host-side action in a recording.
type Step struct {
	Direction     string
	RawHex        string
	HasRaw        bool
	Frame         protocol.Object
	AfterTerminal bool
}

// Transcript is one positive or hostile recording.
type Transcript struct {
	Name       string
	Kind       string
	Phase      string
	Expect     protocol.Object
	Observed   protocol.Object
	Status     string
	ExitStatus int
	CoreOutput []protocol.Object
	HostInput  []Step
}

// Kit is the verified, decoded pinned kit.
type Kit struct {
	Manifest    protocol.Object
	Transcripts []Transcript
	Identities  map[string]protocol.Hash
}

// File returns one pinned kit file, such as "kit/fixtures.jac".
func File(name string) ([]byte, error) { return files.ReadFile(root + name) }

// Load verifies the embedded files against the pinned digests and decodes them.
func Load() (*Kit, error) {
	for name, expected := range protocol.KitDigests {
		data, err := File(name)
		if err != nil {
			return nil, err
		}
		digest := sha256.Sum256(data)
		if hex.EncodeToString(digest[:]) != expected {
			return nil, fmt.Errorf("pinned conformance digest mismatch: %s", name)
		}
	}
	manifest, err := decodeFile("kit/kit.json")
	if err != nil {
		return nil, err
	}
	raw, err := decodeFile("kit/transcripts.json")
	if err != nil {
		return nil, err
	}
	obj, ok := manifest.(protocol.Object)
	entries, isList := raw.([]any)
	if !ok || !isList {
		return nil, errors.New("kit files have an unexpected shape")
	}
	var pending []string
	decisions, _ := obj["pending_decisions"].([]any)
	for _, decision := range decisions {
		entry, _ := decision.(protocol.Object)
		name, _ := entry["name"].(string)
		pending = append(pending, name)
	}
	if obj["protocol"] != protocol.Protocol || obj["carrier"] != protocol.Carrier || obj["core_version"] != protocol.CoreVersion ||
		len(entries) != TranscriptCount || !slices.Equal(pending, protocol.PendingDecisions) {
		return nil, errors.New("kit metadata does not match the pin")
	}
	kit := &Kit{Manifest: obj, Identities: map[string]protocol.Hash{}}
	identities, _ := obj["identities"].(protocol.Object)
	for name, value := range identities {
		if kit.Identities[name], err = protocol.ParseHash(value); err != nil {
			return nil, err
		}
	}
	for _, entry := range entries {
		transcript, err := parseTranscript(entry)
		if err != nil {
			return nil, err
		}
		kit.Transcripts = append(kit.Transcripts, transcript)
	}
	return kit, nil
}

func decodeFile(name string) (any, error) {
	data, err := File(name)
	if err != nil {
		return nil, err
	}
	return protocol.Decode(data, 256)
}

func parseTranscript(value any) (Transcript, error) {
	obj, ok := value.(protocol.Object)
	if !ok {
		return Transcript{}, errors.New("transcript is not an object")
	}
	t := Transcript{}
	t.Name, _ = obj["name"].(string)
	t.Kind, _ = obj["kind"].(string)
	t.Phase, _ = obj["phase"].(string)
	t.Status, _ = obj["status"].(string)
	t.Expect, _ = obj["expect"].(protocol.Object)
	t.Observed, _ = obj["observed"].(protocol.Object)
	status, _ := obj["exit_status"].(int64)
	t.ExitStatus = int(status)
	output, _ := obj["core_output"].([]any)
	for _, frame := range output {
		f, ok := frame.(protocol.Object)
		if !ok {
			return Transcript{}, fmt.Errorf("%s: core_output entry is not an object", t.Name)
		}
		t.CoreOutput = append(t.CoreOutput, f)
	}
	input, _ := obj["host_input"].([]any)
	for _, step := range input {
		s, ok := step.(protocol.Object)
		if !ok {
			return Transcript{}, fmt.Errorf("%s: host_input entry is not an object", t.Name)
		}
		parsed := Step{}
		parsed.Direction, _ = s["direction"].(string)
		parsed.RawHex, parsed.HasRaw = s["raw_hex"].(string)
		parsed.Frame, _ = s["frame"].(protocol.Object)
		parsed.AfterTerminal, _ = s["after_terminal"].(bool)
		t.HostInput = append(t.HostInput, parsed)
	}
	if t.Name == "" || t.Expect == nil || t.Observed == nil {
		return Transcript{}, errors.New("transcript is missing required fields")
	}
	return t, nil
}

// Options configure the replay worker.
type Options struct {
	Command  []string
	Env      []string
	Deadline time.Duration
}

// Replay is the observed result of replaying one recording.
type Replay struct {
	Name             string
	MatchesRecording bool
	Status           string // "conforms" or "diverges"
	Differences      []string
	ProseDrift       []string
	Summary          protocol.Object
	CoreFrames       []protocol.Object
	ExitStatus       *int
	Signal           string
	Stderr           []byte
}

func terminal(frame protocol.Object) bool {
	return slices.Contains([]any{"outcome", "fatal", "shutdown_ack"}, frame["kind"])
}

var prose = []string{"summary", "cause", "next_step", "contrast"}

func stripProse(value any) any {
	switch v := value.(type) {
	case []any:
		out := make([]any, len(v))
		for i, item := range v {
			out[i] = stripProse(item)
		}
		return out
	case protocol.Object:
		out := protocol.Object{}
		for key, item := range v {
			if v["schema"] == "jacquard-diagnostic-v1" && slices.Contains(prose, key) {
				continue
			}
			out[key] = stripProse(item)
		}
		return out
	}
	return value
}

func proseDrift(expected, actual any, path string) []string {
	switch e := expected.(type) {
	case []any:
		a, ok := actual.([]any)
		if !ok {
			return nil
		}
		var out []string
		for i, item := range e {
			var other any
			if i < len(a) {
				other = a[i]
			}
			out = append(out, proseDrift(item, other, fmt.Sprintf("%s/%d", path, i))...)
		}
		return out
	case protocol.Object:
		a, ok := actual.(protocol.Object)
		if !ok {
			return nil
		}
		var out []string
		if e["schema"] == "jacquard-diagnostic-v1" {
			for _, key := range prose {
				if canonical(e[key]) != canonical(a[key]) {
					out = append(out, path+"/"+key)
				}
			}
			return out
		}
		keys := make([]string, 0, len(e))
		for key := range e {
			keys = append(keys, key)
		}
		slices.Sort(keys)
		for _, key := range keys {
			out = append(out, proseDrift(e[key], a[key], path+"/"+key)...)
		}
		return out
	}
	return nil
}

// canonical compares decoded frames. Frames are at most max_json_depth deep,
// so the 256 bound never refuses one (kit.ts uses 64 and would throw); prose
// drift paths are reported in sorted key order, which matches the kit's
// canonical (sorted) JSON.
func canonical(value any) string {
	text, err := protocol.Stringify(value, 256)
	if err != nil {
		return "unencodable: " + err.Error()
	}
	return text
}

func frames(list []protocol.Object) []any {
	out := make([]any, len(list))
	for i, frame := range list {
		out[i] = frame
	}
	return out
}

// ReplayOne drives one recording against the worker and compares what Core
// sends with the recording, modulo non-normative diagnostic prose.
func ReplayOne(ctx context.Context, t Transcript, store string, options Options) Replay {
	replay := Replay{Name: t.Name}
	var observed []protocol.Object
	ended, skipped := false, int64(0)
	var refusal any
	worker, err := carrier.Start(ctx, store, carrier.Options(options))
	if err != nil {
		replay.Differences = []string{err.Error()}
		replay.Status = "diverges"
		return replay
	}
	read := func() (bool, error) {
		frame, err := worker.ReadFrame(protocol.HardLimits)
		if err != nil || frame == nil {
			return false, err
		}
		observed = append(observed, frame)
		ended = ended || terminal(frame)
		return true, nil
	}
	err = func() error {
		for _, step := range t.HostInput {
			switch {
			case step.Direction == "core_to_host":
				if _, err := read(); err != nil {
					return err
				}
			case step.HasRaw:
				data, err := hex.DecodeString(step.RawHex)
				if err != nil {
					return err
				}
				if err := worker.WriteBytes(data); err != nil {
					return err
				}
				worker.CloseInput()
			case step.AfterTerminal && ended:
				refusal = "E1608"
			case ended:
				skipped++
			default:
				data, err := protocol.FrameBytes(step.Frame, protocol.HardLimits.MaxFrameBytes, protocol.HardLimits.MaxJSONDepth)
				if err != nil {
					return err
				}
				var loss *carrier.Loss
				if err := worker.WriteBytes(data); err != nil && !(errors.As(err, &loss) && strings.HasPrefix(loss.Message, "worker write failed")) {
					return err
				}
			}
		}
		worker.CloseInput()
		for {
			more, err := read()
			if err != nil {
				return err
			}
			if !more {
				break
			}
		}
		return worker.Wait()
	}()
	if err != nil {
		replay.Differences = append(replay.Differences, err.Error())
	}
	worker.Dispose()
	replay.CoreFrames, replay.ExitStatus, replay.Signal, replay.Stderr = observed, worker.ExitStatus, worker.Signal, worker.Stderr()

	var end protocol.Object
	for _, frame := range observed {
		if terminal(frame) {
			end = frame
			break
		}
	}
	var result protocol.Object
	if end["kind"] == "outcome" {
		result, _ = end["result"].(protocol.Object)
	}
	var diagnosticList any
	if end["kind"] == "fatal" {
		diagnosticList = end["diagnostics"]
	} else if result["kind"] == "error" {
		diagnosticList = result["diagnostics"]
	}
	var diagnostic protocol.Object
	if list, ok := diagnosticList.([]any); ok && len(list) > 0 {
		diagnostic, _ = list[0].(protocol.Object)
	}
	primary := diagnostic["code"]
	var terminalName any
	switch end["kind"] {
	case "fatal":
		terminalName = "fatal"
	case "shutdown_ack":
		terminalName = "shutdown"
	default:
		evidence, _ := end["evidence"].(protocol.Object)
		core, _ := evidence["core"].(protocol.Object)
		terminalName = core["terminal"]
	}
	requests, terminals := int64(0), int64(0)
	for _, frame := range observed {
		if frame["kind"] == "effect_request" {
			requests++
		}
		if terminal(frame) {
			terminals++
		}
	}
	replay.Summary = protocol.Object{
		"terminal_kind": end["kind"], "primary_code": primary, "terminal": terminalName,
		"effect_requests": requests, "terminal_frames": terminals,
		"host_local_refusal": refusal, "host_inputs_skipped_after_terminal": skipped,
	}
	exitIs := func(code int) bool { return replay.ExitStatus != nil && *replay.ExitStatus == code }
	// As in kit.ts, an absent expectation never equals an observed value or null.
	expects := func(key string, observed any) bool {
		value, present := t.Expect[key]
		return present && value == observed
	}
	sameFrames := canonical(stripProse(frames(observed))) == canonical(stripProse(frames(t.CoreOutput)))
	expect := t.Expect
	var conforms bool
	if t.Kind == "positive" {
		conforms = expects("terminal", terminalName) && exitIs(0) && sameFrames
		for _, step := range t.HostInput {
			if step.Frame["kind"] == "effect_failure" {
				if message, present := step.Frame["message"]; present {
					text, _ := message.(string)
					cause, isText := diagnostic["cause"].(string)
					conforms = conforms && isText && strings.HasSuffix(cause, text)
				}
				break
			}
		}
	} else {
		maximum, bounded := expect["terminal_frames_max"].(int64)
		forbid, _ := expect["forbid_outside_action"].(bool)
		exit := 0
		if expect["code"] == "E1611" {
			exit = 74
		}
		conforms = expects("effect_requests_before_failure", requests) && bounded && terminals <= maximum &&
			(!forbid || requests == 0) && exitIs(exit)
		if t.Phase == "terminal" {
			conforms = conforms && expects("code", refusal) && terminalName == "ok"
		} else {
			conforms = conforms && expects("code", primary)
		}
	}
	replay.Status = "diverges"
	if conforms {
		replay.Status = "conforms"
	}
	if replay.Status != t.Status {
		replay.Differences = append(replay.Differences, "conformance status differs from the recording")
	}
	if !exitIs(t.ExitStatus) {
		replay.Differences = append(replay.Differences, "exit status differs from the recording")
	}
	if canonical(replay.Summary) != canonical(t.Observed) {
		replay.Differences = append(replay.Differences, "observed summary differs from the recording")
	}
	if !sameFrames {
		replay.Differences = append(replay.Differences, "Core frames differ semantically from the recording")
	}
	replay.MatchesRecording = len(replay.Differences) == 0
	replay.ProseDrift = proseDrift(frames(t.CoreOutput), frames(observed), "")
	return replay
}

// ReplayKit replays every recording in order.
func ReplayKit(ctx context.Context, store string, options Options, kit *Kit) ([]Replay, error) {
	if kit == nil {
		var err error
		if kit, err = Load(); err != nil {
			return nil, err
		}
	}
	replays := make([]Replay, 0, len(kit.Transcripts))
	for _, transcript := range kit.Transcripts {
		replays = append(replays, ReplayOne(ctx, transcript, store, options))
	}
	return replays, nil
}

package protocol

import (
	"fmt"
	"slices"
	"strconv"
	"strings"
	"unicode/utf8"
)

// InvocationID is the only invocation identity a v0 host session uses.
const InvocationID = "0000000000000000"

// FailureCategory and Completion form the four permitted effect_failure pairs.
type (
	FailureCategory string
	Completion      string
	CancelReason    string
)

const (
	UnsupportedOperation FailureCategory = "unsupported_operation"
	RefusedAuthority     FailureCategory = "refused_authority"
	OutsideFailure       FailureCategory = "outside_failure"
	CompletionUnknown    FailureCategory = "completion_unknown"

	NotStarted Completion = "not_started"
	Failed     Completion = "failed"
	Unknown    Completion = "unknown"

	Cancelled    CancelReason = "cancelled"
	Timeout      CancelReason = "timeout"
	HostShutdown CancelReason = "host_shutdown"
)

// ValidFailure reports whether category/completion is a permitted pair.
func ValidFailure(category FailureCategory, completion Completion) bool {
	return map[FailureCategory]Completion{
		UnsupportedOperation: NotStarted, RefusedAuthority: NotStarted,
		OutsideFailure: Failed, CompletionUnknown: Unknown,
	}[category] == completion && completion != ""
}

var terminals = []string{"ok", "diagnostic", "cancelled", "host_failure", "completion_unknown"}

// Diagnostic is one jacquard-diagnostic-v1 entry; Raw keeps the exact object.
type Diagnostic struct {
	Code     string // empty when Core sent null
	Domain   string
	Severity string
	Summary  string
	Cause    string
	NextStep string
	Raw      Object
}

// OperationEntry grants one effect operation for the invocation.
type OperationEntry struct{ Effect, Operation Hash }

// EffectRequest is Core asking the host to perform one operation.
type EffectRequest struct {
	Ordinal   int
	Effect    Hash
	Operation Hash
	Arguments []Value
	Raw       Object
}

// Terminal is Core's final frame: an outcome, a fatal, or a shutdown_ack.
type Terminal struct {
	Kind        string // "outcome", "fatal", or "shutdown_ack"
	OK          bool
	Value       Value
	Diagnostics []Diagnostic
	Terminal    string // the evidence terminal for an outcome
	Evidence    Object
	Raw         Object
}

func stringBytes(value any) int {
	switch v := value.(type) {
	case string:
		return len(v)
	case []any:
		total := 0
		for _, item := range v {
			total += stringBytes(item)
		}
		return total
	case Object:
		total := 0
		for _, item := range v {
			total += stringBytes(item)
		}
		return total
	}
	return 0
}

func decodeDiagnostics(value any, limits Limits) ([]Diagnostic, error) {
	entries, err := asArray(value)
	if err != nil {
		return nil, err
	}
	if len(entries) == 0 {
		return nil, malformed("diagnostics must be nonempty")
	}
	if len(entries) > limits.MaxDiagnostics || stringBytes(value) > limits.MaxDiagnosticBytes {
		return nil, exceeded("diagnostics exceed selected limits")
	}
	out := make([]Diagnostic, len(entries))
	for i, entry := range entries {
		obj, err := asObject(entry)
		if err != nil {
			return nil, err
		}
		keys := []string{"cause", "code", "domain", "next_step", "schema", "severity", "span", "summary"}
		if _, ok := obj["contrast"]; ok {
			keys = append(keys, "contrast")
		}
		if _, err := exact(obj, keys...); err != nil {
			return nil, err
		}
		if obj["schema"] != "jacquard-diagnostic-v1" {
			return nil, malformed("unknown diagnostic schema")
		}
		d := Diagnostic{Raw: obj}
		if obj["code"] != nil {
			if d.Code, err = asString(obj["code"]); err != nil {
				return nil, err
			}
		}
		for key, target := range map[string]*string{"cause": &d.Cause, "domain": &d.Domain, "next_step": &d.NextStep, "severity": &d.Severity, "summary": &d.Summary} {
			if *target, err = asString(obj[key]); err != nil {
				return nil, err
			}
		}
		if obj["span"] != nil {
			if _, err := asObject(obj["span"]); err != nil {
				return nil, err
			}
		}
		if contrast, ok := obj["contrast"]; ok && contrast != nil {
			pair, err := exact(contrast, "intended", "mistaken")
			if err != nil {
				return nil, err
			}
			for _, key := range []string{"intended", "mistaken"} {
				if _, err := asString(pair[key]); err != nil {
					return nil, err
				}
			}
		}
		out[i] = d
	}
	return out, nil
}

func sortedHashes(value any, limit int) ([]Hash, error) {
	items, err := asArray(value)
	if err != nil {
		return nil, err
	}
	out := make([]Hash, len(items))
	for i, item := range items {
		if out[i], err = ParseHash(item); err != nil {
			return nil, err
		}
	}
	if len(out) > limit {
		return nil, exceeded("effect set exceeds selected limits")
	}
	if err := strictlySorted(out); err != nil {
		return nil, err
	}
	return out, nil
}

func strictlySorted(hashes []Hash) error {
	for i := 1; i < len(hashes); i++ {
		if hashes[i] <= hashes[i-1] {
			return malformed("effects must be a strictly sorted set")
		}
	}
	return nil
}

func hashList(hashes []Hash) []any {
	out := make([]any, len(hashes))
	for i, h := range hashes {
		out[i] = string(h)
	}
	return out
}

// InvokeFrame validates and renders the single v0 invoke frame.
func InvokeFrame(target Hash, spec Interface, args []Value, operations []OperationEntry, limits Limits) (Object, error) {
	if len(args) != len(spec.Parameters) {
		return nil, malformed("argument count differs from parameter count")
	}
	budget := NewBudget(limits)
	if err := budget.count(len(args), true); err != nil {
		return nil, err
	}
	for _, effect := range spec.Effects {
		if _, err := ParseHash(string(effect)); err != nil {
			return nil, err
		}
	}
	if len(spec.Effects) > limits.MaxEffects {
		return nil, exceeded("effect set exceeds selected limits")
	}
	if err := strictlySorted(spec.Effects); err != nil {
		return nil, err
	}
	if len(operations) > limits.MaxOperations {
		return nil, exceeded("registry exceeds max_operations")
	}
	entries := slices.Clone(operations)
	slices.SortFunc(entries, func(a, b OperationEntry) int {
		return strings.Compare(string(a.Effect+a.Operation), string(b.Effect+b.Operation))
	})
	granted := make([]any, len(entries))
	for i, entry := range entries {
		if _, err := ParseHash(string(entry.Effect)); err != nil {
			return nil, err
		}
		if _, err := ParseHash(string(entry.Operation)); err != nil {
			return nil, err
		}
		if !slices.Contains(spec.Effects, entry.Effect) {
			return nil, malformed("operation effect is outside the interface row")
		}
		if i > 0 && entries[i-1] == entry {
			return nil, malformed("duplicate operation entry")
		}
		granted[i] = Object{"effect": string(entry.Effect), "mode": "once", "operation": string(entry.Operation)}
	}
	encodedArgs := make([]any, len(args))
	for i, arg := range args {
		encoded, err := EncodeValue(arg, budget)
		if err != nil {
			return nil, err
		}
		encodedArgs[i] = encoded
	}
	parameters := make([]any, len(spec.Parameters))
	for i, parameter := range spec.Parameters {
		encoded, err := EncodeType(parameter, budget)
		if err != nil {
			return nil, err
		}
		parameters[i] = encoded
	}
	result, err := EncodeType(spec.Result, budget)
	if err != nil {
		return nil, err
	}
	callable, err := ParseHash(string(target))
	if err != nil {
		return nil, err
	}
	return Object{
		"arguments":     encodedArgs,
		"capabilities":  Object{"effects": hashList(spec.Effects), "operations": granted},
		"interface":     Object{"effects": hashList(spec.Effects), "parameters": parameters, "result": result},
		"invocation_id": InvocationID, "kind": "invoke", "protocol": Protocol,
		"target": Object{"callable": string(callable), "kind": "store-term-v0"},
	}, nil
}

func validateEvidence(value any, limits Limits, budget *Budget) (Object, string, error) {
	evidence, err := exact(value, "core", "host_observations")
	if err != nil {
		return nil, "", err
	}
	core, err := exact(evidence["core"], "capabilities", "effect_requests", "interface", "invocation_id", "schema", "target", "terminal")
	if err != nil {
		return nil, "", err
	}
	terminal, _ := core["terminal"].(string)
	if core["schema"] != "jacquard-host-core-evidence-v0" || !slices.Contains(terminals, terminal) {
		return nil, "", malformed("unknown core evidence schema or terminal")
	}
	if core["invocation_id"] != InvocationID {
		return nil, "", outOfOrder("wrong evidence invocation")
	}
	target, err := exact(core["target"], "callable", "kind")
	if err != nil {
		return nil, "", err
	}
	if _, err := ParseHash(target["callable"]); err != nil {
		return nil, "", err
	}
	if target["kind"] != "store-term-v0" {
		return nil, "", malformed("unknown target kind")
	}
	spec, err := exact(core["interface"], "effects", "parameters", "result")
	if err != nil {
		return nil, "", err
	}
	if _, err := sortedHashes(spec["effects"], limits.MaxEffects); err != nil {
		return nil, "", err
	}
	parameters, err := asArray(spec["parameters"])
	if err != nil {
		return nil, "", err
	}
	if err := budget.count(len(parameters), true); err != nil {
		return nil, "", err
	}
	for _, parameter := range append(slices.Clone(parameters), spec["result"]) {
		if _, err := DecodeType(parameter, budget); err != nil {
			return nil, "", err
		}
	}
	capabilities, err := exact(core["capabilities"], "effects", "operations")
	if err != nil {
		return nil, "", err
	}
	if _, err := sortedHashes(capabilities["effects"], limits.MaxEffects); err != nil {
		return nil, "", err
	}
	operations, err := asArray(capabilities["operations"])
	if err != nil {
		return nil, "", err
	}
	if len(operations) > limits.MaxOperations {
		return nil, "", exceeded("too many evidence operations")
	}
	for _, operation := range operations {
		entry, err := exact(operation, "effect", "mode", "operation")
		if err != nil {
			return nil, "", err
		}
		if err := hashes(entry, "effect", "operation"); err != nil {
			return nil, "", err
		}
		if entry["mode"] != "once" {
			return nil, "", malformed("unknown operation mode")
		}
	}
	requests, err := asArray(core["effect_requests"])
	if err != nil {
		return nil, "", err
	}
	if len(requests) > limits.MaxEffectRequests {
		return nil, "", exceeded("too many evidence requests")
	}
	for i, request := range requests {
		entry, err := exact(request, "effect", "operation", "ordinal")
		if err != nil {
			return nil, "", err
		}
		if err := hashes(entry, "effect", "operation"); err != nil {
			return nil, "", err
		}
		if entry["ordinal"] != int64(i+1) {
			return nil, "", malformed("evidence ordinals must be consecutive")
		}
	}
	observations, err := exact(evidence["host_observations"], "responses", "schema")
	if err != nil {
		return nil, "", err
	}
	if observations["schema"] != "jacquard-host-observations-v0" {
		return nil, "", malformed("unknown host observations schema")
	}
	responses, err := asArray(observations["responses"])
	if err != nil {
		return nil, "", err
	}
	categories := []any{"ok", "unsupported_operation", "refused_authority", "outside_failure", "completion_unknown", "cancelled", "timeout", "host_shutdown"}
	completions := []any{"completed", "not_started", "failed", "unknown"}
	for _, response := range responses {
		entry, err := exact(response, "category", "completion", "ordinal")
		if err != nil {
			return nil, "", err
		}
		if !slices.Contains(categories, entry["category"]) {
			return nil, "", malformed("unknown observation category")
		}
		if !slices.Contains(completions, entry["completion"]) {
			return nil, "", malformed("unknown observation completion")
		}
		ordinal, ok := entry["ordinal"].(int64)
		if !ok || ordinal < 1 || ordinal > int64(len(requests)) {
			return nil, "", malformed("invalid observation ordinal")
		}
	}
	return evidence, terminal, nil
}

func hashes(obj Object, keys ...string) error {
	for _, key := range keys {
		if _, err := ParseHash(obj[key]); err != nil {
			return err
		}
	}
	return nil
}

// ParseCoreFrame validates one Core frame after core_hello. It returns either
// an *EffectRequest or a *Terminal.
func ParseCoreFrame(frame Object, limits Limits) (any, error) {
	kind := frame["kind"]
	if kind == "core_hello" {
		return nil, outOfOrder("core_hello may only be the first frame")
	}
	if frame["protocol"] != Protocol {
		return nil, malformed("unknown protocol")
	}
	switch kind {
	case "fatal":
		if _, err := exact(frame, "diagnostics", "kind", "protocol"); err != nil {
			return nil, err
		}
		diagnostics, err := decodeDiagnostics(frame["diagnostics"], limits)
		if err != nil {
			return nil, err
		}
		return &Terminal{Kind: "fatal", Diagnostics: diagnostics, Raw: frame}, nil
	case "shutdown_ack":
		if _, err := exact(frame, "kind", "protocol"); err != nil {
			return nil, err
		}
		return &Terminal{Kind: "shutdown_ack", Raw: frame}, nil
	case "effect_request":
		if _, err := exact(frame, "arguments", "effect", "invocation_id", "kind", "mode", "operation", "protocol", "request_id"); err != nil {
			return nil, err
		}
		if err := invocation(frame); err != nil {
			return nil, err
		}
		if frame["mode"] != "once" {
			return nil, malformed("unknown operation mode")
		}
		id, err := asString(frame["request_id"])
		if err != nil {
			return nil, err
		}
		if !realPattern.MatchString(id) {
			return nil, malformed("request_id must have 16 lowercase hexadecimal digits")
		}
		ordinal, _ := strconv.ParseUint(id, 16, 64)
		if ordinal < 1 {
			return nil, outOfOrder("request ordinals start at one")
		}
		if ordinal > maxSafeInteger {
			return nil, outOfOrder("request ordinal is not consecutive")
		}
		budget := NewBudget(limits)
		items, err := counted(frame["arguments"], budget, true)
		if err != nil {
			return nil, err
		}
		effect, err := ParseHash(frame["effect"])
		if err != nil {
			return nil, err
		}
		operation, err := ParseHash(frame["operation"])
		if err != nil {
			return nil, err
		}
		args, err := decodeValueItems(items, budget)
		if err != nil {
			return nil, err
		}
		return &EffectRequest{Ordinal: int(ordinal), Effect: effect, Operation: operation, Arguments: args, Raw: frame}, nil
	case "outcome":
		if _, err := exact(frame, "evidence", "invocation_id", "kind", "protocol", "result"); err != nil {
			return nil, err
		}
		if err := invocation(frame); err != nil {
			return nil, err
		}
		// Core encodes evidence types and the result value against one frame budget.
		budget := NewBudget(limits)
		evidence, terminal, err := validateEvidence(frame["evidence"], limits, budget)
		if err != nil {
			return nil, err
		}
		result, err := asObject(frame["result"])
		if err != nil {
			return nil, err
		}
		switch result["kind"] {
		case "ok":
			if _, err := exact(result, "kind", "value"); err != nil {
				return nil, err
			}
			if terminal != "ok" {
				return nil, malformed("ok result has a non-ok terminal")
			}
			value, err := DecodeValue(result["value"], budget)
			if err != nil {
				return nil, err
			}
			return &Terminal{Kind: "outcome", OK: true, Value: value, Terminal: terminal, Evidence: evidence, Raw: frame}, nil
		case "error":
			if _, err := exact(result, "diagnostics", "kind"); err != nil {
				return nil, err
			}
			if terminal == "ok" {
				return nil, malformed("error result has an ok terminal")
			}
			diagnostics, err := decodeDiagnostics(result["diagnostics"], limits)
			if err != nil {
				return nil, err
			}
			return &Terminal{Kind: "outcome", Diagnostics: diagnostics, Terminal: terminal, Evidence: evidence, Raw: frame}, nil
		}
		return nil, malformed("unknown result kind")
	}
	return nil, malformed("unknown Core frame kind")
}

func invocation(frame Object) error {
	id, err := asString(frame["invocation_id"])
	if err != nil {
		return err
	}
	if id != InvocationID {
		return outOfOrder("frame names the wrong invocation")
	}
	return nil
}

// State is the host session state.
type State string

const (
	StateHello        State = "hello"
	StateNegotiating  State = "negotiating"
	StateSelected     State = "selected"
	StateShuttingDown State = "shutting_down"
	StateRunning      State = "running"
	StateWaiting      State = "waiting"
	StateFinishing    State = "finishing"
	StateClosed       State = "closed"
)

// Session is the carrier-independent v0 host state machine: one hello, one
// selection, one invocation or shutdown, consecutive requests, one terminal.
type Session struct {
	State     State
	Limits    Limits
	Requests  []*EffectRequest
	Responses []Object
	Terminal  *Terminal
	Pending   *EffectRequest
}

// NewSession starts a session awaiting core_hello.
func NewSession() *Session { return &Session{State: StateHello, Limits: HardLimits} }

// Receive validates the next Core frame. It returns nil for core_hello, an
// *EffectRequest, or a *Terminal.
func (s *Session) Receive(frame Object) (any, error) {
	if s.State == StateClosed {
		return nil, outOfOrder("Core sent a frame after its terminal")
	}
	if s.State == StateHello {
		if frame["kind"] != "core_hello" {
			return nil, outOfOrder("first Core frame must be core_hello")
		}
		if _, err := exact(frame, "carrier", "kind", "limits", "versions"); err != nil {
			return nil, err
		}
		if frame["carrier"] != Carrier {
			return nil, malformed("unknown carrier")
		}
		versions, err := asArray(frame["versions"])
		if err != nil {
			return nil, err
		}
		if len(versions) == 0 {
			return nil, malformed("versions must be nonempty")
		}
		offered := false
		for _, version := range versions {
			text, err := asString(version)
			if err != nil {
				return nil, err
			}
			offered = offered || text == Protocol
		}
		limits, err := DecodeLimits(frame["limits"])
		if err != nil {
			return nil, err
		}
		if limits != HardLimits {
			return nil, malformed("hello must advertise exactly the frozen limits")
		}
		if !offered {
			return nil, &ProtocolError{"E1600", "Core does not offer " + Protocol}
		}
		s.State = StateNegotiating
		return nil, nil
	}
	parsed, err := ParseCoreFrame(frame, s.Limits)
	if err != nil {
		return nil, err
	}
	if request, ok := parsed.(*EffectRequest); ok {
		if s.State != StateRunning {
			return nil, outOfOrder("effect request without an available invocation slot")
		}
		if request.Ordinal != len(s.Requests)+1 {
			return nil, outOfOrder("request ordinal is not consecutive")
		}
		if request.Ordinal > s.Limits.MaxEffectRequests {
			return nil, exceeded("Core exceeded max_effect_requests")
		}
		s.Requests = append(s.Requests, request)
		s.Pending, s.State = request, StateWaiting
		return request, nil
	}
	terminal := parsed.(*Terminal)
	switch terminal.Kind {
	case "outcome":
		if !slices.Contains([]State{StateRunning, StateWaiting, StateFinishing}, s.State) {
			return nil, outOfOrder("outcome without a live invocation")
		}
	case "fatal":
		if !slices.Contains([]State{StateNegotiating, StateSelected, StateShuttingDown, StateRunning, StateFinishing}, s.State) {
			return nil, outOfOrder("fatal while a response is outstanding")
		}
	case "shutdown_ack":
		if s.State != StateShuttingDown {
			return nil, outOfOrder("shutdown_ack without shutdown")
		}
	}
	s.Pending, s.Terminal, s.State = nil, terminal, StateClosed
	return terminal, nil
}

func (s *Session) require(states ...State) error {
	if !slices.Contains(states, s.State) {
		return outOfOrder("message is not permitted in state %s", s.State)
	}
	return nil
}

// Select chooses the session limits.
func (s *Session) Select(limits Limits) (Object, error) {
	if err := s.require(StateNegotiating); err != nil {
		return nil, err
	}
	if err := limits.Validate(); err != nil {
		return nil, err
	}
	s.Limits, s.State = limits, StateSelected
	return Object{"kind": "host_select", "limits": limits.JSON(), "protocol": Protocol}, nil
}

// Invoke renders the session's single invocation.
func (s *Session) Invoke(target Hash, spec Interface, args []Value, operations []OperationEntry) (Object, error) {
	if err := s.require(StateSelected); err != nil {
		return nil, err
	}
	frame, err := InvokeFrame(target, spec, args, operations, s.Limits)
	if err != nil {
		return nil, err
	}
	s.State = StateRunning
	return frame, nil
}

// Shutdown ends a selected session without invoking.
func (s *Session) Shutdown() (Object, error) {
	if err := s.require(StateSelected); err != nil {
		return nil, err
	}
	s.State = StateShuttingDown
	return Object{"kind": "shutdown", "protocol": Protocol}, nil
}

func (s *Session) respond(fields Object, terminal bool) Object {
	fields["protocol"], fields["invocation_id"] = Protocol, InvocationID
	fields["request_id"] = fmt.Sprintf("%016x", s.Pending.Ordinal)
	s.Responses = append(s.Responses, fields)
	s.Pending = nil
	s.State = StateRunning
	if terminal {
		s.State = StateFinishing
	}
	return fields
}

// RespondOK answers the pending request with a value.
func (s *Session) RespondOK(value Value) (Object, error) {
	if err := s.require(StateWaiting); err != nil {
		return nil, err
	}
	encoded, err := EncodeValue(value, NewBudget(s.Limits))
	if err != nil {
		return nil, err
	}
	return s.respond(Object{"kind": "effect_ok", "value": encoded}, false), nil
}

// RespondFailure answers the pending request with a permitted failure pair.
func (s *Session) RespondFailure(category FailureCategory, completion Completion, message string) (Object, error) {
	if err := s.require(StateWaiting); err != nil {
		return nil, err
	}
	if !ValidFailure(category, completion) {
		return nil, malformed("invalid effect_failure pair")
	}
	if !validScalar(message) {
		return nil, malformed("host message is not valid UTF-8")
	}
	if len(message) > s.Limits.MaxHostMessageBytes {
		return nil, exceeded("host message exceeds max_host_message_bytes")
	}
	return s.respond(Object{"kind": "effect_failure", "category": string(category), "completion": string(completion), "message": message}, true), nil
}

// Cancel withdraws the pending request.
func (s *Session) Cancel(reason CancelReason, completion Completion) (Object, error) {
	if err := s.require(StateWaiting); err != nil {
		return nil, err
	}
	if !slices.Contains([]CancelReason{Cancelled, Timeout, HostShutdown}, reason) || !slices.Contains([]Completion{NotStarted, Failed, Unknown}, completion) {
		return nil, malformed("unknown cancellation enum")
	}
	return s.respond(Object{"kind": "cancel", "reason": string(reason), "completion": string(completion)}, true), nil
}

func validScalar(text string) bool { return utf8.ValidString(text) }

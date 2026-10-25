// Package host invokes checked Jacquard terms through the pinned
// jacquard-host-v0 protocol and serves their effect requests from a closed
// registry of Go handlers. Each invocation runs one fresh, serial worker.
package host

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/jbwinters/jacquard-host/go/internal/carrier"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

// EvidenceSchema names the host-owned observations recorded beside Core frames.
const EvidenceSchema = "jacquard-host-go-evidence-v0"

// Failure is a handler's structured answer that an operation did not succeed.
// Return it from a Handler; any other error is reported as completion unknown.
type Failure struct {
	Category   protocol.FailureCategory
	Completion protocol.Completion
	Message    string
}

func (f *Failure) Error() string {
	return fmt.Sprintf("%s/%s: %s", f.Category, f.Completion, f.Message)
}

// NewFailure returns a Failure, refusing a pair the protocol does not permit.
func NewFailure(category protocol.FailureCategory, completion protocol.Completion, message string) (*Failure, error) {
	if !protocol.ValidFailure(category, completion) {
		return nil, fmt.Errorf("invalid effect_failure pair %s/%s", category, completion)
	}
	return &Failure{category, completion, message}, nil
}

// Unsupported reports an operation the host does not provide (not started).
func Unsupported(message string) *Failure {
	return &Failure{protocol.UnsupportedOperation, protocol.NotStarted, message}
}

// Refused reports an operation the host refused to authorize (not started).
func Refused(message string) *Failure {
	return &Failure{protocol.RefusedAuthority, protocol.NotStarted, message}
}

// OutsideFailed reports an outside action that failed without effect.
func OutsideFailed(message string) *Failure {
	return &Failure{protocol.OutsideFailure, protocol.Failed, message}
}

// CompletionUnknown reports an outside action whose completion is unknown.
func CompletionUnknown(message string) *Failure {
	return &Failure{protocol.CompletionUnknown, protocol.Unknown, message}
}

// Handler performs one effect operation. The context ends when the worker
// expires or is aborted; cancellation is cooperative.
type Handler func(ctx context.Context, arguments []protocol.Value) (protocol.Value, error)

// OperationRegistry is a closed map from exact operation identities to handlers.
type OperationRegistry struct {
	handlers map[protocol.OperationEntry]Handler
}

// NewRegistry returns an empty registry.
func NewRegistry() *OperationRegistry {
	return &OperationRegistry{handlers: map[protocol.OperationEntry]Handler{}}
}

// Bind registers a handler for one effect operation identity.
func (r *OperationRegistry) Bind(effect, operation protocol.Hash, handler Handler) error {
	for _, id := range []protocol.Hash{effect, operation} {
		if _, err := protocol.ParseHash(string(id)); err != nil {
			return err
		}
	}
	if handler == nil {
		return errors.New("handler must not be nil")
	}
	key := protocol.OperationEntry{Effect: effect, Operation: operation}
	if _, bound := r.handlers[key]; bound {
		return errors.New("operation identity is already bound")
	}
	r.handlers[key] = handler
	return nil
}

// Entries lists the bound operations in protocol order.
func (r *OperationRegistry) Entries() []protocol.OperationEntry {
	if r == nil {
		return nil
	}
	entries := make([]protocol.OperationEntry, 0, len(r.handlers))
	for key := range r.handlers {
		entries = append(entries, key)
	}
	slices.SortFunc(entries, func(a, b protocol.OperationEntry) int {
		return strings.Compare(string(a.Effect+a.Operation), string(b.Effect+b.Operation))
	})
	return entries
}

func (r *OperationRegistry) dispatch(ctx context.Context, request *protocol.EffectRequest) (value protocol.Value, err error) {
	var handler Handler
	if r != nil {
		handler = r.handlers[protocol.OperationEntry{Effect: request.Effect, Operation: request.Operation}]
	}
	if handler == nil {
		return nil, Unsupported("operation is not configured")
	}
	defer func() {
		if recover() != nil {
			value, err = nil, CompletionUnknown("handler panicked")
		}
	}()
	value, err = handler(ctx, request.Arguments)
	var failure *Failure
	if err != nil && !errors.As(err, &failure) {
		return nil, CompletionUnknown("handler returned an error")
	}
	if failure != nil {
		if !protocol.ValidFailure(failure.Category, failure.Completion) {
			return nil, CompletionUnknown("handler returned an invalid failure pair")
		}
		return nil, failure
	}
	if value == nil {
		return nil, OutsideFailed("handler returned no value")
	}
	return value, nil
}

// Result kinds.
const (
	KindOK            = "ok"
	KindError         = "error"
	KindFatal         = "fatal"
	KindShutdownAck   = "shutdown_ack"
	KindCarrierLoss   = "carrier_loss"
	KindProtocolError = "protocol_error"
	KindHostError     = "host_error"
)

// InvocationResult retains Core's frames and evidence beside host observations.
type InvocationResult struct {
	Kind         string
	Value        protocol.Value
	Diagnostics  []protocol.Diagnostic
	Terminal     string
	Evidence     protocol.Object
	CoreFrames   []protocol.Object
	HostFrames   []protocol.Object
	Requests     []*protocol.EffectRequest
	ExitStatus   *int
	Signal       string
	Stderr       []byte
	HostEvidence protocol.Object
}

// OK reports a successful outcome.
func (r *InvocationResult) OK() bool { return r.Kind == KindOK }

// Code is the first Core diagnostic code, else the host evidence code.
func (r *InvocationResult) Code() string {
	if len(r.Diagnostics) > 0 && r.Diagnostics[0].Code != "" {
		return r.Diagnostics[0].Code
	}
	code, _ := r.HostEvidence["code"].(string)
	return code
}

// Message summarizes the result for people.
func (r *InvocationResult) Message() string {
	if r.OK() {
		return "Jacquard invocation succeeded"
	}
	var detail string
	if len(r.Diagnostics) > 0 {
		detail = r.Diagnostics[0].Summary + " " + r.Diagnostics[0].Cause
	} else if text, ok := r.HostEvidence["detail"].(string); ok {
		detail = text
	} else {
		detail = r.Kind
	}
	code := r.Code()
	if code == "" {
		code = r.Kind
	}
	return code + ": " + detail
}

// Unwrap returns the value or an *InvocationError.
func (r *InvocationResult) Unwrap() (protocol.Value, error) {
	if !r.OK() {
		return nil, &InvocationError{Result: r}
	}
	return r.Value, nil
}

// Classifications reachable with errors.Is from an *InvocationError.
var (
	ErrDeadlineExceeded = errors.New("host deadline exceeded")
	ErrWorkerLost       = errors.New("worker lost")
	ErrOperationFailed  = errors.New("operation failed")
)

// InvocationError is an unsuccessful InvocationResult.
type InvocationError struct{ Result *InvocationResult }

func (e *InvocationError) Error() string { return e.Result.Message() }

// Origin is "core" when Core reported diagnostics, else "host".
func (e *InvocationError) Origin() string {
	if len(e.Result.Diagnostics) > 0 {
		return "core"
	}
	return "host"
}

// Unwrap exposes the classification for errors.Is.
func (e *InvocationError) Unwrap() error {
	switch {
	case e.Result.HostEvidence["reason"] == "deadline":
		return ErrDeadlineExceeded
	case e.Result.Kind == KindCarrierLoss:
		return ErrWorkerLost
	case e.Result.Terminal == "host_failure" || e.Result.Terminal == "completion_unknown":
		return ErrOperationFailed
	}
	return nil
}

// Options configure a Host.
type Options struct {
	// Command is the Core executable and leading arguments; defaults to
	// $JACQUARD_BIN, then "jacquard".
	Command []string
	// Env replaces the inherited environment when non-nil.
	Env []string
	// Deadline bounds each worker lifetime, including handlers (default 30s).
	Deadline time.Duration
	// Limits selects session limits; nil selects the hard limits.
	Limits *protocol.Limits
}

// Host invokes terms in one prepared store.
type Host struct {
	Store   string
	Limits  protocol.Limits
	options carrier.Options
}

// New validates options before any worker starts.
func New(store string, options Options) (*Host, error) {
	limits := protocol.HardLimits
	if options.Limits != nil {
		limits = *options.Limits
	}
	if err := limits.Validate(); err != nil {
		return nil, err
	}
	co := carrier.Options{Command: options.Command, Env: options.Env, Deadline: options.Deadline}
	if err := co.Validate(); err != nil {
		return nil, err
	}
	return &Host{Store: store, Limits: limits, options: co}, nil
}

// Invoke runs target with args. The complete invoke frame is validated before a
// worker starts. Cancelling ctx kills the worker; nothing is retried.
func (h *Host) Invoke(ctx context.Context, target protocol.Hash, spec protocol.Interface, args []protocol.Value, registry *OperationRegistry) *InvocationResult {
	frame, err := protocol.InvokeFrame(target, spec, args, registry.Entries(), h.Limits)
	if err == nil {
		_, err = protocol.FrameBytes(frame, h.Limits.MaxFrameBytes, h.Limits.MaxJSONDepth)
	}
	var protocolError *protocol.ProtocolError
	if errors.As(err, &protocolError) {
		return &InvocationResult{Kind: KindHostError, HostEvidence: protocol.Object{
			"schema": EvidenceSchema, "reason": "invoke_refused_locally", "code": protocolError.Code, "detail": protocolError.Message,
		}}
	} else if err != nil {
		return &InvocationResult{Kind: KindHostError, HostEvidence: protocol.Object{
			"schema": EvidenceSchema, "reason": "invoke_refused_locally", "detail": err.Error(),
		}}
	}
	return h.run(ctx, func(s *protocol.Session) (protocol.Object, error) {
		return s.Invoke(target, spec, args, registry.Entries())
	}, registry)
}

// ShutdownProbe negotiates a session and shuts it down without invoking.
func (h *Host) ShutdownProbe(ctx context.Context) *InvocationResult {
	return h.run(ctx, (*protocol.Session).Shutdown, nil)
}

type answer struct {
	value protocol.Value
	err   error
}

func (h *Host) run(ctx context.Context, first func(*protocol.Session) (protocol.Object, error), registry *OperationRegistry) *InvocationResult {
	session := protocol.NewSession()
	result := &InvocationResult{Kind: KindCarrierLoss}
	worker, err := carrier.Start(ctx, h.Store, h.options)
	if err != nil {
		result.HostEvidence = protocol.Object{"schema": EvidenceSchema, "reason": "invalid_options", "detail": err.Error()}
		return result
	}
	sending := false
	send := func(frame protocol.Object, limits protocol.Limits) error {
		sending = true
		if err := worker.WriteFrame(frame, limits); err != nil {
			return err
		}
		result.HostFrames = append(result.HostFrames, frame)
		sending = false
		return nil
	}
	err = func() error {
		hello, err := worker.ReadFrame(protocol.HardLimits)
		if err != nil {
			return err
		}
		if hello == nil {
			return &carrier.Loss{Message: "worker exited before core_hello"}
		}
		result.CoreFrames = append(result.CoreFrames, hello)
		if _, err := session.Receive(hello); err != nil {
			return err
		}
		selected, err := session.Select(h.Limits)
		if err != nil {
			return err
		}
		if err := send(selected, protocol.HardLimits); err != nil {
			return err
		}
		worker.SetStderrLimit(h.Limits.MaxStderrBytes)
		opening, err := first(session)
		if err != nil {
			return err
		}
		if err := send(opening, session.Limits); err != nil {
			return err
		}
		for session.State != protocol.StateClosed {
			frame, err := worker.ReadFrame(session.Limits)
			if err != nil {
				return err
			}
			if frame == nil {
				return &carrier.Loss{Message: "worker exited before a terminal frame"}
			}
			result.CoreFrames = append(result.CoreFrames, frame)
			parsed, err := session.Receive(frame)
			if err != nil {
				return err
			}
			switch v := parsed.(type) {
			case *protocol.EffectRequest:
				response, err := h.respond(worker, session, registry, v)
				if err != nil {
					return err
				}
				if err := send(response, session.Limits); err != nil {
					return err
				}
			case *protocol.Terminal:
				switch v.Kind {
				case "outcome":
					result.Kind = KindError
					if v.OK {
						result.Kind = KindOK
					}
					result.Value, result.Diagnostics, result.Terminal, result.Evidence = v.Value, v.Diagnostics, v.Terminal, v.Evidence
				case "fatal":
					result.Kind, result.Diagnostics = KindFatal, v.Diagnostics
				case "shutdown_ack":
					result.Kind = KindShutdownAck
				}
			}
		}
		worker.CloseInput()
		extra, err := worker.ReadFrame(session.Limits)
		if err != nil {
			return err
		}
		if extra != nil {
			result.CoreFrames = append(result.CoreFrames, extra)
			if _, err := session.Receive(extra); err != nil {
				return err
			}
		}
		if err := worker.Wait(); err != nil {
			return err
		}
		if worker.ExitStatus == nil || *worker.ExitStatus != 0 {
			return &carrier.Loss{Message: "worker exited unsuccessfully after its terminal"}
		}
		return nil
	}()
	if err != nil {
		var protocolError *protocol.ProtocolError
		var loss *carrier.Loss
		isProtocol := errors.As(err, &protocolError)
		code, reason := "E1611", "carrier_loss"
		switch {
		case isProtocol && sending:
			result.Kind, code, reason = KindHostError, protocolError.Code, "host_frame_refused"
		case isProtocol:
			result.Kind, code, reason = KindProtocolError, protocolError.Code, "core_frame_refused"
		default:
			result.Kind = KindCarrierLoss
			if errors.As(err, &loss) && loss.Timeout {
				reason = "deadline"
			} else if worker.SpawnError != nil {
				reason = "worker_spawn_failed"
			} else if loss != nil && loss.Aborted {
				reason = "aborted"
			}
		}
		var outstanding any
		if session.Pending != nil {
			outstanding = int64(session.Pending.Ordinal)
		}
		result.HostEvidence = protocol.Object{
			"schema": EvidenceSchema, "code": code, "reason": reason, "detail": err.Error(),
			"state": string(session.State), "outstanding_request": outstanding,
		}
	}
	worker.Dispose()
	result.Requests = slices.Clone(session.Requests)
	result.ExitStatus, result.Signal, result.Stderr = worker.ExitStatus, worker.Signal, worker.Stderr()
	return result
}

// respond runs the handler, bounded by the worker lifetime, and renders the
// response frame. A handler still running at the deadline is abandoned: no
// late response is sent and nothing is retried.
func (h *Host) respond(worker *carrier.Worker, session *protocol.Session, registry *OperationRegistry, request *protocol.EffectRequest) (protocol.Object, error) {
	done := make(chan answer, 1)
	go func() {
		value, err := registry.dispatch(worker.Context(), request)
		done <- answer{value, err}
	}()
	var reply answer
	select {
	case reply = <-done:
	case <-worker.Done():
		return nil, worker.Fault()
	}
	if fault := worker.Fault(); fault != nil {
		return nil, fault
	}
	if reply.err == nil {
		encoded, err := protocol.EncodeValue(reply.value, protocol.NewBudget(session.Limits))
		if err == nil {
			_, err = protocol.FrameBytes(protocol.Object{
				"kind": "effect_ok", "invocation_id": protocol.InvocationID, "protocol": protocol.Protocol,
				"request_id": fmt.Sprintf("%016x", request.Ordinal), "value": encoded,
			}, session.Limits.MaxFrameBytes, session.Limits.MaxJSONDepth)
		}
		if err == nil {
			return session.RespondOK(reply.value)
		}
		reply.err = OutsideFailed("handler result has no encoding within the selected limits")
	}
	failure := reply.err.(*Failure)
	return session.RespondFailure(failure.Category, failure.Completion, bounded(failure.Message, session.Limits.MaxHostMessageBytes))
}

// bounded returns valid UTF-8 of at most limit bytes, cut at a rune boundary.
func bounded(text string, limit int) string {
	text = strings.ToValidUTF8(text, "�")
	if len(text) <= limit {
		return text
	}
	end := limit
	for end > 0 && !utf8.RuneStart(text[end]) {
		end--
	}
	return text[:end]
}

// Package carrier runs one bounded stdio-u32-json-v0 worker process.
package carrier

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"sync"
	"time"

	"github.com/jbwinters/jacquard-host/go/protocol"
)

// DefaultDeadline is the worker lifetime when Options.Deadline is zero.
const DefaultDeadline = 30 * time.Second

// Loss reports that the carrier failed: the worker could not start, closed a
// pipe, exited unsuccessfully, was aborted, or outlived its deadline.
type Loss struct {
	Message string
	Timeout bool
	Aborted bool
}

func (e *Loss) Error() string { return e.Message }

// Options configure one worker lifetime.
type Options struct {
	// Command is the Core executable and leading arguments; "host worker
	// --store DIR" is appended. Defaults to $JACQUARD_BIN, then "jacquard".
	Command []string
	// Env replaces the inherited environment when non-nil.
	Env []string
	// Deadline bounds the whole lifetime: spawn, pipes, handlers, and exit.
	Deadline time.Duration
}

// Validate refuses an empty command or a nonpositive deadline.
func (o Options) Validate() error {
	if o.Deadline < 0 {
		return errors.New("deadline must be positive")
	}
	for _, item := range o.Command {
		if item == "" {
			return errors.New("command must be a nonempty executable argument list")
		}
	}
	return nil
}

func (o Options) command() []string {
	if len(o.Command) > 0 {
		return o.Command
	}
	if binary := os.Getenv("JACQUARD_BIN"); binary != "" {
		return []string{binary}
	}
	return []string{"jacquard"}
}

// Worker is one child process. Reads and writes are synchronous; the lifetime
// deadline or the parent context kills the child, which unblocks them.
type Worker struct {
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	stdout io.ReadCloser
	stderr *boundedBuffer
	ctx    context.Context
	parent context.Context
	cancel context.CancelFunc
	stop   func() bool

	mu         sync.Mutex
	fault      error
	waited     bool
	reaped     bool
	inputDone  bool
	SpawnError error
	ExitStatus *int
	Signal     string
}

// Start spawns the worker. A spawn failure is returned by the first operation,
// not by Start, so callers record it like any other carrier loss.
func Start(parent context.Context, store string, options Options) (*Worker, error) {
	if err := options.Validate(); err != nil {
		return nil, err
	}
	deadline := options.Deadline
	if deadline == 0 {
		deadline = DefaultDeadline
	}
	ctx, cancel := context.WithTimeout(parent, deadline)
	command := options.command()
	w := &Worker{ctx: ctx, parent: parent, cancel: cancel, stderr: &boundedBuffer{limit: protocol.HardLimits.MaxStderrBytes}}
	w.cmd = exec.Command(command[0], append(append([]string{}, command[1:]...), "host", "worker", "--store", store)...)
	if options.Env != nil {
		w.cmd.Env = options.Env
	}
	w.cmd.Stderr = w.stderr
	w.cmd.WaitDelay = time.Second
	isolate(w.cmd)
	var err error
	if w.stdin, err = w.cmd.StdinPipe(); err == nil {
		if w.stdout, err = w.cmd.StdoutPipe(); err != nil {
			_ = w.stdin.Close()
		}
	}
	if err == nil {
		err = w.cmd.Start()
	}
	if err != nil {
		w.SpawnError = err
		w.fault = &Loss{Message: fmt.Sprintf("could not start %s (%v); check the configured Core installation", command[0], err)}
		w.waited = true
		return w, nil
	}
	w.stop = context.AfterFunc(ctx, w.expire)
	return w, nil
}

// expire kills the worker's process group and closes both pipes, so a read or
// write blocked on a descendant that inherited them returns at the deadline.
func (w *Worker) expire() {
	w.mu.Lock()
	reaped := w.reaped
	w.mu.Unlock()
	if !reaped {
		killTree(w.cmd.Process)
	}
	_ = w.stdout.Close()
	_ = w.stdin.Close()
}

// Context is cancelled when the worker fails, expires, or is disposed.
// Handlers receive it for cooperative cancellation.
func (w *Worker) Context() context.Context { return w.ctx }

// Done is closed when the worker context ends.
func (w *Worker) Done() <-chan struct{} { return w.ctx.Done() }

// Fault returns the carrier failure that ended the worker, if any.
func (w *Worker) Fault() error {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.fault == nil && w.ctx.Err() != nil {
		if w.parent.Err() != nil {
			w.fault = &Loss{Message: "invocation aborted; outside completion is unknown", Aborted: true}
		} else {
			w.fault = &Loss{Message: "worker lifetime deadline exceeded", Timeout: true}
		}
	}
	return w.fault
}

func (w *Worker) failed(fallback string) error {
	if fault := w.Fault(); fault != nil {
		return fault
	}
	return &Loss{Message: fallback}
}

// SetStderrLimit lowers the retained stderr ceiling; stderr is always drained.
func (w *Worker) SetStderrLimit(limit int) { w.stderr.setLimit(limit) }

// Stderr returns the retained stderr prefix.
func (w *Worker) Stderr() []byte { return w.stderr.bytes() }

// ReadFrame reads one frame. It returns nil at a clean end of stream.
func (w *Worker) ReadFrame(limits protocol.Limits) (protocol.Object, error) {
	if fault := w.Fault(); fault != nil {
		return nil, fault
	}
	var prefix [4]byte
	n, err := io.ReadFull(w.stdout, prefix[:])
	if err != nil {
		if n == 0 && errors.Is(err, io.EOF) && w.Fault() == nil {
			return nil, nil
		}
		return nil, w.failed("worker closed stdout during a frame")
	}
	length := binary.BigEndian.Uint32(prefix[:])
	if length == 0 || length > uint32(limits.MaxFrameBytes) {
		return nil, &protocol.ProtocolError{Code: "E1602", Message: "frame length exceeds max_frame_bytes"}
	}
	payload := make([]byte, length)
	if _, err := io.ReadFull(w.stdout, payload); err != nil {
		return nil, w.failed("worker closed stdout during a frame")
	}
	if fault := w.Fault(); fault != nil {
		return nil, fault
	}
	return protocol.DecodeObject(payload, limits.MaxJSONDepth)
}

// WriteBytes writes raw carrier bytes.
func (w *Worker) WriteBytes(data []byte) error {
	if fault := w.Fault(); fault != nil {
		return fault
	}
	if _, err := w.stdin.Write(data); err != nil {
		return w.failed(fmt.Sprintf("worker write failed (%v)", err))
	}
	return w.Fault()
}

// WriteFrame encodes and writes one frame under the given limits.
func (w *Worker) WriteFrame(frame protocol.Object, limits protocol.Limits) error {
	data, err := protocol.FrameBytes(frame, limits.MaxFrameBytes, limits.MaxJSONDepth)
	if err != nil {
		return err
	}
	return w.WriteBytes(data)
}

// CloseInput closes the worker's stdin.
func (w *Worker) CloseInput() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if !w.inputDone && w.stdin != nil {
		w.inputDone = true
		_ = w.stdin.Close()
	}
}

// Wait closes stdin and waits for the process, bounded by the deadline. Call
// it only after stdout has reached end of stream.
func (w *Worker) Wait() error {
	w.CloseInput()
	w.reap()
	return w.Fault()
}

func (w *Worker) reap() {
	w.mu.Lock()
	if w.waited {
		w.mu.Unlock()
		return
	}
	w.waited = true
	w.mu.Unlock()
	started := time.Now()
	err := w.cmd.Wait()
	// ErrWaitDelay means a descendant still held the output pipes after a
	// successful exit: fail closed. An unsuccessful exit status masks that
	// error (the run already fails), so a wait that lasted WaitDelay also
	// stops any remaining group members.
	if held := errors.Is(err, exec.ErrWaitDelay); held || time.Since(started) >= w.cmd.WaitDelay {
		killTree(w.cmd.Process)
		w.mu.Lock()
		if held && w.fault == nil {
			w.fault = &Loss{Message: "worker descendants kept its pipes open after exit"}
		}
		w.mu.Unlock()
	}
	w.mu.Lock()
	w.reaped = true
	w.mu.Unlock()
	state := w.cmd.ProcessState
	if state == nil {
		return
	}
	if w.Signal = signalName(state); w.Signal != "" {
		return
	}
	code := state.ExitCode()
	w.ExitStatus = &code
}

// Dispose kills a live child, reaps it, and releases the deadline timer.
func (w *Worker) Dispose() {
	if w.stop != nil {
		w.stop()
	}
	w.CloseInput()
	w.mu.Lock()
	live := !w.waited
	w.mu.Unlock()
	if live {
		killTree(w.cmd.Process)
		w.reap()
	}
	w.cancel()
}

// boundedBuffer drains every write and retains only a prefix.
type boundedBuffer struct {
	mu    sync.Mutex
	data  []byte
	limit int
}

func (b *boundedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if room := b.limit - len(b.data); room > 0 {
		b.data = append(b.data, p[:min(room, len(p))]...)
	}
	return len(p), nil
}

func (b *boundedBuffer) setLimit(limit int) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.limit = limit
	if len(b.data) > limit {
		b.data = b.data[:limit]
	}
}

func (b *boundedBuffer) bytes() []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]byte(nil), b.data...)
}

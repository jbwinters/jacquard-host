// Package jacquard calls checked Jacquard functions from Go: it prepares and
// caches programs with Core's public commands, maps native Go values, binds
// Go services to exact operation identities, and keeps the raw evidence of
// every invocation. Each invocation runs one fresh, serial worker.
package jacquard

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

const programSchema = "jacquard-go-program-v1"

// DefaultCacheDir holds prepared programs unless PrepareOptions.CacheDir is set.
const DefaultCacheDir = ".scratch/jacquard-host-go"

// RuntimeOptions configure Core discovery and invocation bounds.
type RuntimeOptions struct {
	// Binary is the Core executable; defaults to $JACQUARD_BIN, then "jacquard".
	Binary string
	// Prelude is Core's prelude directory; defaults to $JACQUARD_PRELUDE.
	Prelude string
	// Deadline bounds each invocation's worker lifetime (default 30s).
	Deadline time.Duration
	// Limits selects session limits; nil selects the hard limits.
	Limits *protocol.Limits
}

// Runtime prepares programs against one Core installation.
type Runtime struct {
	binary   string
	prelude  string
	deadline time.Duration
	limits   protocol.Limits

	mu       sync.Mutex
	discover sync.Mutex // serializes the version check and prelude discovery
	checked  map[string]bool
	preludes map[string][]group
	// command runs a Core subcommand; tests may observe it.
	command func(ctx context.Context, binary string, args ...string) (string, error)
}

// NewRuntime validates options; it does not run Core yet.
func NewRuntime(options RuntimeOptions) (*Runtime, error) {
	binary := options.Binary
	if binary == "" {
		binary = os.Getenv("JACQUARD_BIN")
	}
	if binary == "" {
		binary = "jacquard"
	}
	prelude := options.Prelude
	if prelude == "" {
		prelude = os.Getenv("JACQUARD_PRELUDE")
	}
	if prelude == "" {
		return nil, &BindingError{Message: "set JACQUARD_PRELUDE or RuntimeOptions.Prelude; Core and prelude must match the compatibility pin"}
	}
	absolute, err := filepath.Abs(prelude)
	if err != nil {
		return nil, &BindingError{Message: err.Error()}
	}
	if options.Deadline < 0 {
		return nil, &BindingError{Message: "deadline must be positive"}
	}
	limits := protocol.HardLimits
	if options.Limits != nil {
		limits = *options.Limits
	}
	if err := limits.Validate(); err != nil {
		return nil, &BindingError{Message: "invalid runtime limits: " + err.Error()}
	}
	r := &Runtime{binary: binary, prelude: absolute, deadline: options.Deadline, limits: limits,
		checked: map[string]bool{}, preludes: map[string][]group{}}
	r.command = r.run
	return r, nil
}

func (r *Runtime) env() []string {
	return append(os.Environ(), "JACQUARD_PRELUDE="+r.prelude)
}

func (r *Runtime) run(ctx context.Context, binary string, args ...string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	command := exec.CommandContext(ctx, binary, args...)
	command.Env = r.env()
	var stdout, stderr limitedBuffer
	stdout.limit, stderr.limit = 8<<20, 4096
	command.Stdout, command.Stderr = &stdout, &stderr
	if err := command.Run(); err != nil {
		detail := strings.TrimSpace(stderr.String())
		if detail == "" {
			detail = err.Error()
		}
		return "", &PreparationError{Message: fmt.Sprintf("Core %s failed: %s", args[0], detail), Err: err}
	}
	if stdout.overflow {
		return "", &PreparationError{Message: fmt.Sprintf("Core %s output exceeds 8 MiB", args[0])}
	}
	return stdout.String(), nil
}

type limitedBuffer struct {
	bytes.Buffer
	limit    int
	overflow bool
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	if room := b.limit - b.Len(); room < len(p) {
		b.overflow = true
		b.Buffer.Write(p[:max(room, 0)])
		return len(p), nil
	}
	return b.Buffer.Write(p)
}

// PrepareOptions configure preparation.
type PrepareOptions struct {
	// CacheDir holds verified prepared programs (default DefaultCacheDir).
	CacheDir string
}

type metadata struct {
	Identity     map[string]string `json:"identity"`
	Names        [][2]string       `json:"names"`
	Constructors [][2]string       `json:"constructors"`
	Signatures   map[string]string `json:"signatures"`
	Groups       []group           `json:"groups"`
	Files        map[string]string `json:"files"`
}

func digest(data []byte) string {
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

func fileDigest(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return digest(data), nil
}

// tree digests every regular file under root, refusing other entry kinds.
func tree(root string) (map[string]string, error) {
	files := map[string]string{}
	err := filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return nil
		}
		relative, _ := filepath.Rel(root, path)
		if !entry.Type().IsRegular() {
			return &PreparationError{Message: "unsupported filesystem entry in prepared artifacts: " + relative}
		}
		files[filepath.ToSlash(relative)], err = fileDigest(path)
		return err
	})
	return files, err
}

func canonical(value any) string {
	data, _ := json.Marshal(value) // maps marshal with sorted keys
	return string(data)
}

func (r *Runtime) executable() (string, error) {
	path, err := exec.LookPath(r.binary)
	if err != nil {
		return "", &PreparationError{Message: fmt.Sprintf("cannot find Core executable %s; configure JACQUARD_BIN using the setup guide", r.binary), Err: err}
	}
	if path, err = filepath.Abs(path); err == nil {
		path, err = filepath.EvalSymlinks(path)
	}
	if err != nil {
		return "", &PreparationError{Message: err.Error(), Err: err}
	}
	return path, nil
}

// Prepare checks, hashes, and installs first-party declaration sources (.jac
// files, or .jqd files, in dependency order) into a verified cache keyed by the
// source bytes, Core executable bytes, prelude contents, and protocol.
func (r *Runtime) Prepare(ctx context.Context, sources []string, options PrepareOptions) (*Program, error) {
	program, err := r.prepare(ctx, sources, options)
	var preparation *PreparationError
	var binding *BindingError
	if err != nil && !errors.As(err, &preparation) && !errors.As(err, &binding) {
		return nil, &PreparationError{Message: "could not prepare program: " + err.Error(), Err: err}
	}
	return program, err
}

func (r *Runtime) prepare(ctx context.Context, sources []string, options PrepareOptions) (*Program, error) {
	if len(sources) == 0 {
		return nil, &PreparationError{Message: "pass .jac files or .jqd files in dependency order"}
	}
	syntax := filepath.Ext(sources[0])
	for _, source := range sources {
		if (syntax != ".jac" && syntax != ".jqd") || filepath.Ext(source) != syntax {
			return nil, &PreparationError{Message: "pass .jac files or .jqd files in dependency order"}
		}
	}
	cacheDir := options.CacheDir
	if cacheDir == "" {
		cacheDir = DefaultCacheDir
	}
	cache, err := filepath.Abs(cacheDir)
	if err != nil {
		return nil, err
	}
	binary, err := r.executable()
	if err != nil {
		return nil, err
	}
	binaryHash, err := fileDigest(binary)
	if err != nil {
		return nil, err
	}
	if err := r.checkVersion(ctx, binary, binaryHash); err != nil {
		return nil, err
	}
	preludeFiles, err := tree(r.prelude)
	if err != nil {
		return nil, &PreparationError{Message: "cannot read prelude " + r.prelude, Err: err}
	}
	preludeHash := digest([]byte(canonical(preludeFiles)))
	var content []byte
	for i, source := range sources {
		data, err := os.ReadFile(source)
		if err != nil {
			return nil, &PreparationError{Message: "cannot read " + source, Err: err}
		}
		if i > 0 {
			content = append(content, '\n')
		}
		content = append(content, data...)
	}
	identity := map[string]string{"schema": programSchema, "protocol": protocol.Protocol, "source": digest(content),
		"binary": binaryHash, "prelude": preludeHash, "syntax": syntax}
	directory := filepath.Join(cache, digest([]byte(canonical(identity))))
	if err := os.MkdirAll(cache, 0o755); err != nil {
		return nil, err
	}
	if _, err := os.Stat(directory); err == nil {
		return r.load(directory, identity, binary)
	}
	staging, err := os.MkdirTemp(cache, "prepare-")
	if err != nil {
		return nil, err
	}
	defer os.RemoveAll(staging)
	snapshot := filepath.Join(staging, "source"+syntax)
	if err := os.WriteFile(snapshot, content, 0o644); err != nil {
		return nil, err
	}
	output, err := r.command(ctx, binary, "check", snapshot, "--print-sigs")
	if err != nil {
		return nil, err
	}
	signatures := map[string]string{}
	for _, line := range strings.Split(output, "\n") {
		if name, signature, ok := strings.Cut(strings.TrimSpace(line), " : "); ok && !strings.ContainsAny(name, " \t") {
			signatures[name] = signature
		}
	}
	groups, err := r.preludeGroups(ctx, binary, binaryHash+preludeHash)
	if err != nil {
		return nil, err
	}
	output, err = r.command(ctx, binary, "hash", snapshot)
	if err != nil {
		return nil, err
	}
	local, err := hashGroups(output)
	if err != nil {
		return nil, err
	}
	groups = append(slices.Clone(groups), local...)
	if err := checkGroups(groups); err != nil {
		return nil, err
	}
	if _, err := r.command(ctx, binary, "run", snapshot, "--store", filepath.Join(staging, "store")); err != nil {
		return nil, err
	}
	meta := metadata{Identity: identity, Signatures: signatures, Groups: groups}
	seen := map[string]bool{}
	constructors := map[string]protocol.Hash{}
	var order []string
	for _, g := range groups {
		for _, e := range g {
			if !seen[e.Name] {
				seen[e.Name] = true
				meta.Names = append(meta.Names, [2]string{e.Name, string(e.Hash)})
			}
			if _, ok := constructors[e.Name]; !ok {
				order = append(order, e.Name)
			}
			constructors[e.Name] = e.Hash
		}
	}
	for _, name := range order {
		meta.Constructors = append(meta.Constructors, [2]string{name, string(constructors[name])})
	}
	if meta.Files, err = tree(staging); err != nil {
		return nil, err
	}
	encoded, err := json.Marshal(meta)
	if err != nil {
		return nil, err
	}
	if err := os.WriteFile(filepath.Join(staging, "program.json"), encoded, 0o644); err != nil {
		return nil, err
	}
	if err := os.Rename(staging, directory); err != nil {
		// A concurrent preparation published the same artifact first.
		if _, statErr := os.Stat(directory); statErr != nil {
			return nil, err
		}
	}
	return r.load(directory, identity, binary)
}

// checkVersion runs `--version` once per executable digest, even when
// preparations start concurrently.
func (r *Runtime) checkVersion(ctx context.Context, binary, binaryHash string) error {
	r.discover.Lock()
	defer r.discover.Unlock()
	r.mu.Lock()
	checked := r.checked[binaryHash]
	r.mu.Unlock()
	if checked {
		return nil
	}
	version, err := r.command(ctx, binary, "--version")
	if err != nil {
		return err
	}
	if fields := strings.Fields(version); len(fields) == 0 || fields[len(fields)-1] != protocol.CoreVersion {
		return &PreparationError{Message: fmt.Sprintf("expected pinned Core version %s, found %q; the exact release is also required", protocol.CoreVersion, strings.TrimSpace(version))}
	}
	r.mu.Lock()
	r.checked[binaryHash] = true
	r.mu.Unlock()
	return nil
}

func (r *Runtime) preludeGroups(ctx context.Context, binary, key string) ([]group, error) {
	r.discover.Lock()
	defer r.discover.Unlock()
	r.mu.Lock()
	cached, ok := r.preludes[key]
	r.mu.Unlock()
	if ok {
		return cached, nil
	}
	var groups []group
	for _, name := range []string{"01-prim.jqd", "02-data.jqd", "19-audit.jqd"} {
		output, err := r.command(ctx, binary, "hash", filepath.Join(r.prelude, name))
		if err != nil {
			return nil, err
		}
		parsed, err := hashGroups(output)
		if err != nil {
			return nil, err
		}
		groups = append(groups, parsed...)
	}
	r.mu.Lock()
	r.preludes[key] = groups
	r.mu.Unlock()
	return groups, nil
}

func (r *Runtime) load(directory string, identity map[string]string, binary string) (*Program, error) {
	fail := func(err error) (*Program, error) {
		return nil, &PreparationError{Message: fmt.Sprintf("prepared cache %s is incomplete or changed; remove that cache entry and prepare again", directory), Err: err}
	}
	data, err := os.ReadFile(filepath.Join(directory, "program.json"))
	if err != nil {
		return fail(err)
	}
	var meta metadata
	if err := json.Unmarshal(data, &meta); err != nil {
		return fail(err)
	}
	observed, err := tree(directory)
	if err != nil {
		return fail(err)
	}
	delete(observed, "program.json")
	if canonical(meta.Identity) != canonical(identity) || canonical(observed) != canonical(meta.Files) {
		return fail(errors.New("cached artifact digests do not match"))
	}
	if err := checkGroups(meta.Groups); err != nil {
		return nil, err
	}
	return newProgram(r, directory, meta, binary)
}

// Program is a prepared, verified program.
type Program struct {
	Runtime   *Runtime
	Directory string
	Data      *Data
	host      *host.Host

	interfaces map[string]protocol.Interface
	effects    map[string]effectGroup
	services   map[string]any
}

type effectGroup struct {
	identity   protocol.Hash
	operations []entry
}

func newProgram(r *Runtime, directory string, meta metadata, binary string) (*Program, error) {
	names, constructors := map[string]protocol.Hash{}, map[string]protocol.Hash{}
	for _, pair := range meta.Names {
		names[pair[0]] = protocol.Hash(pair[1])
	}
	for _, pair := range meta.Constructors {
		constructors[pair[0]] = protocol.Hash(pair[1])
	}
	h, err := host.New(filepath.Join(directory, "store"), host.Options{Command: []string{binary}, Env: r.env(), Deadline: r.deadline, Limits: &r.limits})
	if err != nil {
		return nil, &BindingError{Message: err.Error()}
	}
	p := &Program{Runtime: r, Directory: directory, Data: newData(names, constructors, r.limits), host: h,
		interfaces: map[string]protocol.Interface{}, effects: map[string]effectGroup{}, services: map[string]any{}}
	used := map[protocol.Hash]bool{}
	for name, signature := range meta.Signatures {
		if spec, err := parseSignature(signature, names); err == nil {
			p.interfaces[name] = spec
			for _, effect := range spec.Effects {
				used[effect] = true
			}
		}
	}
	for _, g := range meta.Groups {
		for _, e := range g {
			if used[e.Hash] {
				var operations []entry
				for _, other := range g {
					if other.Hash != e.Hash {
						operations = append(operations, other)
					}
				}
				p.effects[e.Name] = effectGroup{e.Hash, operations}
			}
		}
	}
	return p, nil
}

// Operations is an explicit service: operation names (store spelling such as
// "lookup-stock", or Go spelling such as "LookupStock") mapped to functions.
type Operations map[string]any

// Provide configures services shared by every function bound afterwards.
func (p *Program) Provide(services map[string]any) error {
	for name, service := range services {
		if _, ok := p.effects[normal(name)]; !ok {
			return &BindingError{Message: "unknown effect " + name}
		}
		p.services[normal(name)] = service
	}
	return nil
}

// FunctionOptions configure one bound function.
type FunctionOptions struct {
	// Effects supplies services for this function's checked effects, by
	// effect name, overriding Provide.
	Effects map[string]any
}

// Function binds a checked public function. Every operation of each checked
// root effect must have a handler before the first invocation.
func (p *Program) Function(name string, options FunctionOptions) (*Function, error) {
	spec, ok := p.interfaces[name]
	if !ok {
		return nil, &BindingError{Message: name + ": absent or unsupported closed v0 signature"}
	}
	target, ok := p.Data.names[name]
	if !ok {
		return nil, &BindingError{Message: name + ": no public identity"}
	}
	selected := map[string]effectGroup{}
	for effectName, effect := range p.effects {
		if slices.Contains(spec.Effects, effect.identity) {
			selected[effectName] = effect
		}
	}
	if len(selected) != len(spec.Effects) {
		return nil, &BindingError{Message: name + ": effect metadata is unavailable; use the low-level host package"}
	}
	services := map[string]any{}
	for effectName, service := range p.services {
		services[effectName] = service
	}
	for effectName, service := range options.Effects {
		if _, ok := selected[normal(effectName)]; !ok {
			return nil, &BindingError{Message: fmt.Sprintf("%s does not use %s", name, effectName)}
		}
		services[normal(effectName)] = service
	}
	registry := host.NewRegistry()
	var missing []string
	effectNames := make([]string, 0, len(selected))
	for effectName := range selected {
		effectNames = append(effectNames, effectName)
	}
	slices.Sort(effectNames)
	for _, effectName := range effectNames {
		effect := selected[effectName]
		for _, operation := range effect.operations {
			handler, ok := p.handler(services[effectName], operation.Name)
			if !ok {
				missing = append(missing, goName(effectName)+"."+goName(operation.Name))
				continue
			}
			if err := registry.Bind(effect.identity, operation.Hash, handler); err != nil {
				return nil, &BindingError{Message: err.Error()}
			}
		}
	}
	if len(missing) > 0 {
		return nil, &BindingError{Message: fmt.Sprintf("%s: missing handlers: %s. Bind every operation in each declared effect before calling", name, strings.Join(missing, ", "))}
	}
	return &Function{Program: p, Name: name, Signature: spec, target: target, registry: registry}, nil
}

var contextType = reflect.TypeFor[context.Context]()

// handler adapts a Go method or function to a host.Handler. Arguments decode
// into the method's parameter types; an optional leading context.Context
// receives cancellation. Results may be (T), (T, error), (error), or none for
// unit; a *host.Failure error is reported as that failure.
func (p *Program) handler(service any, operation string) (host.Handler, bool) {
	if service == nil {
		return nil, false
	}
	var fn reflect.Value
	if ops, ok := service.(Operations); ok {
		for _, key := range []string{operation, goName(operation)} {
			if f, ok := ops[key]; ok {
				fn = reflect.ValueOf(f)
				break
			}
		}
	} else {
		fn = reflect.ValueOf(service).MethodByName(goName(operation))
	}
	if !fn.IsValid() || fn.Kind() != reflect.Func {
		return nil, false
	}
	t := fn.Type()
	withContext := t.NumIn() > 0 && t.In(0) == contextType
	first := 0
	if withContext {
		first = 1
	}
	if t.IsVariadic() || t.NumOut() > 2 || (t.NumOut() == 2 && t.Out(1) != errorType) {
		return nil, false
	}
	return func(ctx context.Context, arguments []protocol.Value) (protocol.Value, error) {
		if len(arguments) != t.NumIn()-first {
			return nil, host.OutsideFailed("handler parameter count differs from the request")
		}
		in := make([]reflect.Value, 0, t.NumIn())
		if withContext {
			in = append(in, reflect.ValueOf(ctx))
		}
		for i, argument := range arguments {
			value, err := p.Data.decode(argument, t.In(first+i), nil)
			if err != nil {
				return nil, host.OutsideFailed("handler argument cannot be mapped")
			}
			in = append(in, value)
		}
		out := fn.Call(in)
		var result reflect.Value
		switch len(out) {
		case 1:
			if t.Out(0) == errorType {
				if !out[0].IsNil() {
					return nil, out[0].Interface().(error)
				}
			} else {
				result = out[0]
			}
		case 2:
			if !out[1].IsNil() {
				return nil, out[1].Interface().(error)
			}
			result = out[0]
		}
		if !result.IsValid() {
			return protocol.Tuple{}, nil
		}
		value, err := p.Data.encode(result, nil, "handler result", 3)
		if err != nil {
			return nil, host.OutsideFailed("handler result cannot be represented")
		}
		return value, nil
	}, true
}

// Function is a bound checked function.
type Function struct {
	Program   *Program
	Name      string
	Signature protocol.Interface
	target    protocol.Hash
	registry  *host.OperationRegistry
}

func (f *Function) arguments(args []any) ([]protocol.Value, error) {
	if len(args) != len(f.Signature.Parameters) {
		return nil, &InvalidArgument{Message: fmt.Sprintf("%s: expected %d arguments, received %d", f.Name, len(f.Signature.Parameters), len(args))}
	}
	values := make([]protocol.Value, len(args))
	for i, arg := range args {
		value, err := f.Program.Data.encode(reflect.ValueOf(arg), f.Signature.Parameters[i], fmt.Sprintf("%s.argument[%d]", f.Name, i), 3)
		if err != nil {
			return nil, err
		}
		values[i] = value
	}
	limits := f.Program.host.Limits
	frame, err := protocol.InvokeFrame(f.target, f.Signature, values, f.registry.Entries(), limits)
	if err == nil {
		_, err = protocol.FrameBytes(frame, limits.MaxFrameBytes, limits.MaxJSONDepth)
	}
	if err != nil {
		return nil, &InvalidArgument{Message: fmt.Sprintf("%s: %v; reduce input size or use smaller independent batches", f.Name, err)}
	}
	return values, nil
}

// Invoke validates the arguments locally, then runs one worker. The error is
// non-nil only for a local refusal; Core and carrier failures are in the
// Invocation. Cancelling ctx kills the worker; nothing is retried.
func (f *Function) Invoke(ctx context.Context, args ...any) (*Invocation, error) {
	values, err := f.arguments(args)
	if err != nil {
		return nil, err
	}
	return &Invocation{Raw: f.Program.host.Invoke(ctx, f.target, f.Signature, values, f.registry), function: f}, nil
}

// Call invokes fn and decodes its result into R. Use any for native values.
func Call[R any](ctx context.Context, fn *Function, args ...any) (R, error) {
	var zero R
	invocation, err := fn.Invoke(ctx, args...)
	if err != nil {
		return zero, err
	}
	var out R
	if err := invocation.Decode(&out); err != nil {
		return zero, err
	}
	return out, nil
}

// Invocation is one completed invocation with its raw evidence.
type Invocation struct {
	Raw      *host.InvocationResult
	function *Function
}

// OK reports a successful Core outcome.
func (i *Invocation) OK() bool { return i.Raw.OK() }

// Code is the primary diagnostic or host code.
func (i *Invocation) Code() string { return i.Raw.Code() }

// Message summarizes the result.
func (i *Invocation) Message() string { return i.Raw.Message() }

// Err returns nil on success, else a *host.InvocationError; classify it with
// errors.Is and host.ErrDeadlineExceeded, host.ErrWorkerLost, or
// host.ErrOperationFailed.
func (i *Invocation) Err() error {
	_, err := i.Raw.Unwrap()
	return err
}

// Decode stores the result in target (a non-nil pointer). A mapping failure
// after a successful Core result is a *BindingError retaining Raw.
func (i *Invocation) Decode(target any) error {
	value, err := i.Raw.Unwrap()
	if err != nil {
		return err
	}
	if err := i.function.Program.Data.Decode(value, i.function.Signature.Result, target); err != nil {
		return &BindingError{Message: fmt.Sprintf("%s: Core completed, but result mapping failed: %v", i.function.Name, err), Result: i.Raw}
	}
	return nil
}

// Value decodes the result into native Go values.
func (i *Invocation) Value() (any, error) {
	var out any
	err := i.Decode(&out)
	return out, err
}

// Render lists the outcome and each effect request by operation name.
func (i *Invocation) Render() string {
	status := "ok"
	if !i.OK() {
		status = i.Message()
	}
	lines := []string{i.function.Name + ": " + status}
	for _, request := range i.Raw.Requests {
		label, ok := i.function.Program.Data.labels[request.Operation]
		if !ok {
			label = string(request.Operation)
		}
		lines = append(lines, fmt.Sprintf("  %d. %s", request.Ordinal, label))
	}
	return strings.Join(lines, "\n")
}

// Save writes the invocation's frames and host evidence. The record contains
// argument and result values; it is written only when called.
func (i *Invocation) Save(path string) error {
	frames := func(list []protocol.Object) []any {
		out := make([]any, len(list))
		for n, frame := range list {
			out[n] = frame
		}
		return out
	}
	var exit any
	if i.Raw.ExitStatus != nil {
		exit = int64(*i.Raw.ExitStatus)
	}
	var signal any
	if i.Raw.Signal != "" {
		signal = i.Raw.Signal
	}
	evidence := i.Raw.HostEvidence
	if evidence == nil {
		evidence = protocol.Object{}
	}
	text, err := protocol.Stringify(protocol.Object{
		"schema": "jacquard-host-inspection-v1", "function": i.function.Name,
		"core_frames": frames(i.Raw.CoreFrames), "host_frames": frames(i.Raw.HostFrames),
		"host": protocol.Object{"evidence": evidence, "exit_status": exit, "signal": signal,
			"stderr_base64": base64.StdEncoding.EncodeToString(i.Raw.Stderr)},
	}, 128)
	if err != nil {
		return err
	}
	return os.WriteFile(path, []byte(text+"\n"), 0o600)
}

// MapBatches runs a pure function whose final parameter and result are Lists
// over explicit chunks of the final argument, which must be a slice. Every
// chunk is validated before the first runs. On failure it returns the decoded
// values of completed chunks and a *BatchError; nothing is retried.
func MapBatches[R any](ctx context.Context, fn *Function, batchSize int, args ...any) ([]R, error) {
	if batchSize < 1 {
		return nil, &InvalidArgument{Message: "batch size must be positive"}
	}
	list := fn.Program.Data.names["list"]
	params := fn.Signature.Parameters
	var last protocol.BoundaryType
	if len(params) > 0 {
		last = params[len(params)-1]
	}
	lastNominal, okLast := last.(protocol.Nominal)
	result, okResult := fn.Signature.Result.(protocol.Nominal)
	if len(fn.Signature.Effects) > 0 || !okLast || lastNominal.Identity != list || !okResult || result.Identity != list {
		return nil, &BindingError{Message: "MapBatches needs a pure function with a final List parameter and a List result"}
	}
	if len(args) == 0 {
		return nil, &InvalidArgument{Message: "MapBatches needs a final slice argument"}
	}
	rows := reflect.ValueOf(args[len(args)-1])
	if rows.Kind() != reflect.Slice {
		return nil, &InvalidArgument{Message: "MapBatches needs a final slice argument"}
	}
	var chunks [][]any
	for start := 0; start < rows.Len(); start += batchSize {
		chunk := append(slices.Clone(args[:len(args)-1]), rows.Slice(start, min(start+batchSize, rows.Len())).Interface())
		chunks = append(chunks, chunk)
	}
	if _, err := fn.arguments(append(slices.Clone(args[:len(args)-1]), rows.Slice(0, 0).Interface())); err != nil {
		return nil, err
	}
	for _, chunk := range chunks {
		if _, err := fn.arguments(chunk); err != nil {
			return nil, err
		}
	}
	values := []R{}
	for index, chunk := range chunks {
		batch, err := Call[[]R](ctx, fn, chunk...)
		if err != nil {
			return values, &BatchError{Index: index, Err: err}
		}
		values = append(values, batch...)
	}
	return values, nil
}

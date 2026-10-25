package jacquard

import (
	"context"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"

	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

type liveSuite struct {
	runtime *Runtime
	cache   string
	root    string
}

func live(t *testing.T) liveSuite {
	t.Helper()
	if os.Getenv("JACQUARD_BIN") == "" || os.Getenv("JACQUARD_PRELUDE") == "" {
		t.Skip("live tests need JACQUARD_BIN and JACQUARD_PRELUDE")
	}
	runtime, err := NewRuntime(RuntimeOptions{})
	if err != nil {
		t.Fatal(err)
	}
	root := t.TempDir()
	return liveSuite{runtime, filepath.Join(root, "cache"), root}
}

func example(project string) string {
	return filepath.Join("..", "..", "examples", project, "rules.jac")
}

func (s liveSuite) prepare(t *testing.T, sources ...string) *Program {
	t.Helper()
	program, err := s.runtime.Prepare(context.Background(), sources, PrepareOptions{CacheDir: s.cache})
	if err != nil {
		t.Fatal(err)
	}
	return program
}

func (s liveSuite) source(t *testing.T, name, text string) string {
	t.Helper()
	path := filepath.Join(s.root, name)
	if err := os.WriteFile(path, []byte(text), 0o644); err != nil {
		t.Fatal(err)
	}
	return path
}

func function(t *testing.T, p *Program, name string, effects map[string]any) *Function {
	t.Helper()
	fn, err := p.Function(name, FunctionOptions{Effects: effects})
	if err != nil {
		t.Fatal(err)
	}
	return fn
}

func TestLiveQuickstartCallsInvocationsAndSavedFrames(t *testing.T) {
	s := live(t)
	quick := s.prepare(t, example("quickstart"))
	shipping := function(t, quick, "shipping-cost", nil)
	for total, expected := range map[int]int{35: 5, 75: 0} {
		if cost, err := Call[int](context.Background(), shipping, total); err != nil || cost != expected {
			t.Fatalf("shipping(%d) = %d, %v", total, cost, err)
		}
	}
	invocation, err := shipping.Invoke(context.Background(), 35)
	if err != nil {
		t.Fatal(err)
	}
	if value, err := invocation.Value(); err != nil || value != int64(5) || invocation.Render() != "shipping-cost: ok" {
		t.Fatalf("%v %v %q", value, err, invocation.Render())
	}
	path := filepath.Join(s.root, "inspection.json")
	if err := invocation.Save(path); err != nil {
		t.Fatal(err)
	}
	raw, _ := os.ReadFile(path)
	saved, err := protocol.DecodeObject(raw[:len(raw)-1], 128)
	if err != nil || saved["schema"] != "jacquard-host-inspection-v1" || len(saved["core_frames"].([]any)) != len(invocation.Raw.CoreFrames) {
		t.Fatalf("saved %v %v", saved["schema"], err)
	}
}

type triagePolicy struct{ Page, Stale int }
type liveIncident struct {
	ID, Service   string
	Severity, Age int
	Visible       bool
	Owner         *string
}
type urgencyName string
type liveRouted struct {
	ID, Team string
	Urgency  urgencyName
	Reasons  []string
}
type liveInvalid struct{ ID string }
type report interface{ report() }

func (liveRouted) report()  {}
func (liveInvalid) report() {}

func triageProgram(t *testing.T, s liveSuite) (*Program, *Function) {
	p := s.prepare(t, example("triage"))
	d := p.Data
	for name, prototype := range map[string]any{"TriagePolicy": triagePolicy{}, "Incident": liveIncident{}, "Routed": liveRouted{}, "InvalidIncident": liveInvalid{}} {
		if err := d.Record(name, prototype); err != nil {
			t.Fatal(err)
		}
	}
	if err := RegisterEnum(d, map[urgencyName]string{"PAGE": "PageNow", "TODAY": "WorkToday", "WATCH": "WatchOnly"}); err != nil {
		t.Fatal(err)
	}
	if err := RegisterUnion[report](d, liveRouted{}, liveInvalid{}); err != nil {
		t.Fatal(err)
	}
	return p, function(t, p, "triage.batch", nil)
}

func incidentRow(i int) liveIncident {
	var owner *string
	if i%3 != 0 {
		ops := "ops"
		owner = &ops
	}
	return liveIncident{fmt.Sprint(i), "api", i%5 + 1, i * 3, i%2 == 0, owner}
}

func TestLiveRecordsEnumsOptionsAndExplicitBatches(t *testing.T) {
	s := live(t)
	_, route := triageProgram(t, s)
	policy := triagePolicy{4, 60}
	rows := make([]liveIncident, 64)
	for i := range rows {
		rows[i] = incidentRow(i)
	}
	results, err := MapBatches[report](context.Background(), route, 8, policy, rows)
	if err != nil || len(results) != len(rows) {
		t.Fatalf("%d results, %v", len(results), err)
	}
	for i, row := range rows {
		page := row.Visible && row.Severity >= 4
		expected := liveRouted{ID: row.ID, Team: "api-oncall", Urgency: "WATCH", Reasons: []string{}}
		if row.Owner != nil {
			expected.Team = *row.Owner
		} else {
			expected.Reasons = append(expected.Reasons, "unowned")
		}
		if row.Age >= 60 {
			expected.Reasons = append(expected.Reasons, "ageing")
		}
		if page {
			expected.Reasons = append(expected.Reasons, "customer-impact")
		}
		switch {
		case page:
			expected.Urgency = "PAGE"
		case row.Age >= 60 || row.Severity == 5:
			expected.Urgency = "TODAY"
		}
		got := results[i].(liveRouted)
		if got.ID != expected.ID || got.Team != expected.Team || got.Urgency != expected.Urgency || !slices.Equal(got.Reasons, expected.Reasons) {
			t.Fatalf("row %d: %+v, want %+v", i, got, expected)
		}
	}
	if empty, err := MapBatches[report](context.Background(), route, 8, policy, []liveIncident{}); err != nil || len(empty) != 0 {
		t.Fatalf("empty batch: %v %v", empty, err)
	}
}

func TestLiveInvalidInputIsRefusedBeforeAnyWorker(t *testing.T) {
	s := live(t)
	p, route := triageProgram(t, s)
	refusing, err := host.New(p.host.Store, host.Options{Command: []string{"/no/such/jacquard"}})
	if err != nil {
		t.Fatal(err)
	}
	p.host = refusing // a spawn would now fail as worker_spawn_failed, not InvalidArgument
	rows := make([]liveIncident, 17)
	for i := range rows {
		rows[i] = incidentRow(i)
	}
	rows[16].Service = "\xff"
	_, err = MapBatches[report](context.Background(), route, 8, triagePolicy{4, 60}, rows)
	var argument *InvalidArgument
	if !errors.As(err, &argument) || !strings.Contains(err.Error(), "argument[1][0].Service") {
		t.Fatalf("late invalid row: %v", err)
	}
	many := make([]liveIncident, 64)
	if _, err := Call[[]report](context.Background(), route, triagePolicy{4, 60}, many); !errors.As(err, &argument) {
		t.Fatalf("over-deep single call: %v", err)
	}
}

type partialInventory struct{ calls int }

func (p *partialInventory) BookingStatus(any) Variant { p.calls++; return Variant{} }

func TestLiveMissingHandlersRefuseBindingAndPureCallsNeedNoServices(t *testing.T) {
	s := live(t)
	fulfillment := s.prepare(t, example("fulfillment"))
	inventory := &partialInventory{}
	_, err := fulfillment.Function("fulfillment.place", FunctionOptions{Effects: map[string]any{"Inventory": inventory}})
	var binding *BindingError
	if !errors.As(err, &binding) || !strings.Contains(err.Error(), "Inventory.LookupStock") || !strings.Contains(err.Error(), "Dispatch.PublishBooking") {
		t.Fatalf("binding: %v", err)
	}
	if inventory.calls != 0 {
		t.Fatal("handler ran during binding")
	}
	if err := fulfillment.Provide(map[string]any{"Inventory": inventory}); err != nil {
		t.Fatal(err)
	}
	if valid, err := Call[bool](context.Background(), function(t, fulfillment, "order.valid-lines", nil), []any{}, []string{}); err != nil || !valid {
		t.Fatalf("pure call: %v %v", valid, err)
	}
	if err := fulfillment.Provide(map[string]any{"Unknown": inventory}); !errors.As(err, &binding) {
		t.Fatalf("unknown effect: %v", err)
	}
}

func TestLiveUnknownCompletionHappensOnceWithRawEvidence(t *testing.T) {
	s := live(t)
	fulfillment := s.prepare(t, example("fulfillment"))
	d := fulfillment.Data
	calls := 0
	warehouse := Operations{
		"booking-status": func(any) (Variant, error) { return d.Make("BookingPending") },
		"lookup-stock":   func(string) (Variant, error) { return d.Make("Stock", 20) },
		"reserve-order": func(ctx context.Context, order any) (Variant, error) {
			calls++
			return d.Make("Booked", "receipt-1")
		},
		"publish-booking": func(string, string) error { return errors.New("synthetic after commit") },
	}
	place := function(t, fulfillment, "fulfillment.place", map[string]any{"Inventory": warehouse, "Dispatch": warehouse})
	order, _ := d.Make("Order", "one", []Variant{{Tag: "line", Fields: []any{"sensor", 2}}})
	_, err := Call[any](context.Background(), place, order)
	var failure *host.InvocationError
	if !errors.As(err, &failure) || !errors.Is(err, host.ErrOperationFailed) || failure.Result.Code() != "E1612" {
		t.Fatalf("expected E1612 operation failure, got %v", err)
	}
	sawOK := slices.ContainsFunc(failure.Result.HostFrames, func(frame protocol.Object) bool { return frame["kind"] == "effect_ok" })
	if !sawOK || calls != 1 {
		t.Fatalf("effect_ok sent %v, reserve calls %d", sawOK, calls)
	}
}

type job struct {
	ID                    string
	Effort, Value, Latest int
}
type assignment struct {
	ID   string
	Slot int
}
type plan struct {
	Score       int
	Assignments []assignment
}
type planned struct{ Plan plan }
type invalidPlan struct{ Reason string }
type planning interface{ planning() }

func (planned) planning()     {}
func (invalidPlan) planning() {}

func optimum(jobs []job, caps []int, i int) int {
	if i == len(jobs) {
		return 0
	}
	best := optimum(jobs, caps, i+1)
	for slot := 0; slot < min(jobs[i].Latest, len(caps)); slot++ {
		if caps[slot] >= jobs[i].Effort {
			next := slices.Clone(caps)
			next[slot] -= jobs[i].Effort
			best = max(best, jobs[i].Value+optimum(jobs, next, i+1))
		}
	}
	return best
}

func TestLivePlannerMatchesAnIndependentExhaustiveOracle(t *testing.T) {
	s := live(t)
	p := s.prepare(t, example("planner"))
	for name, prototype := range map[string]any{"Job": job{}, "Assignment": assignment{}, "Plan": plan{}, "Planned": planned{}, "InvalidPlan": invalidPlan{}} {
		if err := p.Data.Record(name, prototype); err != nil {
			t.Fatal(err)
		}
	}
	if err := RegisterUnion[planning](p.Data, planned{}, invalidPlan{}); err != nil {
		t.Fatal(err)
	}
	solve := function(t, p, "planner.plan", nil)
	for seed := 0; seed < 25; seed++ {
		jobs := make([]job, seed%6+1)
		for i := range jobs {
			jobs[i] = job{fmt.Sprintf("job-%d", i), (seed+i*3)%4 + 1, (seed*7 + i*11) % 15, (seed+i)%3 + 1}
		}
		caps := []int{seed%5 + 1, seed%3 + 2, 3}[:seed%3+1]
		result, err := Call[planning](context.Background(), solve, jobs, caps)
		if err != nil {
			t.Fatal(err)
		}
		got, ok := result.(planned)
		if !ok || got.Plan.Score != optimum(jobs, caps, 0) {
			t.Fatalf("seed %d: %+v, optimum %d", seed, result, optimum(jobs, caps, 0))
		}
		loads, score, seen := make([]int, len(caps)), 0, map[string]bool{}
		for _, a := range got.Plan.Assignments {
			index := slices.IndexFunc(jobs, func(j job) bool { return j.ID == a.ID })
			if index < 0 || seen[a.ID] || a.Slot < 1 || a.Slot > min(jobs[index].Latest, len(caps)) {
				t.Fatalf("seed %d: infeasible assignment %+v", seed, a)
			}
			seen[a.ID] = true
			loads[a.Slot-1] += jobs[index].Effort
			score += jobs[index].Value
		}
		for i, load := range loads {
			if load > caps[i] {
				t.Fatalf("seed %d: slot %d overloaded", seed, i+1)
			}
		}
		if score != got.Plan.Score {
			t.Fatalf("seed %d: score %d != assignments %d", seed, got.Plan.Score, score)
		}
	}
}

func TestLiveCacheReuseInvalidationAndCorruption(t *testing.T) {
	s := live(t)
	quick := s.prepare(t, example("quickstart"))
	command := s.runtime.command
	s.runtime.command = func(context.Context, string, ...string) (string, error) {
		return "", errors.New("unexpected Core command")
	}
	repeated := s.prepare(t, example("quickstart"))
	s.runtime.command = command
	if repeated.Directory != quick.Directory {
		t.Fatal("warm preparation used a different cache entry")
	}
	original, _ := os.ReadFile(example("quickstart"))
	changed := s.source(t, "changed.jac", strings.ReplaceAll(string(original), "\n", "\r\n")+"\r\n-- changed\r\n")
	program := s.prepare(t, changed)
	snapshot, _ := os.ReadFile(filepath.Join(program.Directory, "source.jac"))
	if program.Directory == quick.Directory || !strings.Contains(string(snapshot), "\r\n") {
		t.Fatal("source change did not produce a byte-exact new entry")
	}
	if err := os.WriteFile(filepath.Join(program.Directory, "store", "unexpected"), []byte("corrupt"), 0o644); err != nil {
		t.Fatal(err)
	}
	_, err := s.runtime.Prepare(context.Background(), []string{changed}, PrepareOptions{CacheDir: s.cache})
	var preparation *PreparationError
	if !errors.As(err, &preparation) {
		t.Fatalf("corrupt cache accepted: %v", err)
	}
}

func TestLiveFullIntRangeRealsAndNestedOptions(t *testing.T) {
	s := live(t)
	p := s.prepare(t, s.source(t, "values.jac", "value.int : (Int) ->{} Int\nvalue.int(x) = x\nvalue.real : (Real) ->{} Real\nvalue.real(x) = x\nvalue.option : (Option (Option Int)) ->{} Option (Option Int)\nvalue.option(x) = x\n"))
	ctx := context.Background()
	integer := function(t, p, "value.int", nil)
	for _, value := range []int64{protocol.IntMin, protocol.IntMax} {
		if got, err := Call[int64](ctx, integer, value); err != nil || got != value {
			t.Fatalf("Int %d -> %d %v", value, got, err)
		}
	}
	var argument *InvalidArgument
	if _, err := Call[int64](ctx, integer, uint64(protocol.IntMax)+1); !errors.As(err, &argument) {
		t.Fatalf("out-of-range Int: %v", err)
	}
	real := function(t, p, "value.real", nil)
	for _, value := range []float64{1, math.Copysign(0, -1), math.Inf(1), math.NaN()} {
		got, err := Call[float64](ctx, real, value)
		if err != nil || math.Float64bits(got) != math.Float64bits(value) {
			t.Fatalf("Real %v -> %v %v", value, got, err)
		}
	}
	option := function(t, p, "value.option", nil)
	if got, err := Call[any](ctx, option, nil); err != nil || got != nil {
		t.Fatalf("None -> %v %v", got, err)
	}
	if got, err := Call[any](ctx, option, Some{nil}); err != nil || got != (Some{nil}) {
		t.Fatalf("Some(None) -> %#v %v", got, err)
	}
	if got, err := Call[any](ctx, option, Some{Some{3}}); err != nil || got != int64(3) {
		t.Fatalf("Some(Some(3)) -> %#v %v", got, err)
	}
	three := 3
	inner := &three
	if got, err := Call[**int](ctx, option, &inner); err != nil || got == nil || *got == nil || **got != 3 {
		t.Fatalf("typed nested option: %v %v", got, err)
	}
}

func TestLiveSpawnFailuresAndCancellation(t *testing.T) {
	s := live(t)
	quick := s.prepare(t, example("quickstart"))
	shipping := function(t, quick, "shipping-cost", nil)
	working := quick.host
	quick.host, _ = host.New(working.Store, host.Options{Command: []string{"/no/such/jacquard"}})
	_, err := Call[int](context.Background(), shipping, 10)
	var failure *host.InvocationError
	if !errors.As(err, &failure) || !errors.Is(err, host.ErrWorkerLost) || failure.Result.HostEvidence["reason"] != "worker_spawn_failed" {
		t.Fatalf("spawn failure: %v", err)
	}
	quick.host = working
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	invocation, err := shipping.Invoke(ctx, 10)
	if err != nil || invocation.Raw.HostEvidence["reason"] != "aborted" {
		t.Fatalf("cancelled: %v %v", err, invocation.Raw.HostEvidence)
	}
}

func TestLiveOutputOverflowFailsOnceWithoutAdaptiveBatching(t *testing.T) {
	s := live(t)
	_, route := triageProgram(t, s)
	rows := make([]liveIncident, 29)
	for i := range rows {
		rows[i] = liveIncident{fmt.Sprint(i), "api", 5, 90, true, nil}
	}
	values, err := MapBatches[report](context.Background(), route, 29, triagePolicy{4, 60}, rows)
	var batch *BatchError
	var failure *host.InvocationError
	if !errors.As(err, &batch) || batch.Index != 0 || len(values) != 0 || !errors.As(err, &failure) || failure.Result.Code() != "E1602" {
		t.Fatalf("overflow: %v (%d values)", err, len(values))
	}
}

func TestLiveResultMappingFailureRetainsTheSuccessfulResult(t *testing.T) {
	s := live(t)
	shipping := function(t, s.prepare(t, example("quickstart")), "shipping-cost", nil)
	invocation, err := shipping.Invoke(context.Background(), 35)
	if err != nil || !invocation.OK() {
		t.Fatal(err)
	}
	var text string
	err = invocation.Decode(&text)
	var binding *BindingError
	if !errors.As(err, &binding) || binding.Result != invocation.Raw || !binding.Result.OK() {
		t.Fatalf("mapping failure: %v", err)
	}
}

func TestLivePreludeContentsKeyTheCacheAndConcurrentPreparationPublishesOnce(t *testing.T) {
	s := live(t)
	quick := s.prepare(t, example("quickstart"))
	copy := filepath.Join(s.root, "prelude")
	if err := os.CopyFS(copy, os.DirFS(os.Getenv("JACQUARD_PRELUDE"))); err != nil {
		t.Fatal(err)
	}
	copied, err := NewRuntime(RuntimeOptions{Prelude: copy})
	if err != nil {
		t.Fatal(err)
	}
	before, err := copied.Prepare(context.Background(), []string{example("quickstart")}, PrepareOptions{CacheDir: s.cache})
	if err != nil || before.Directory != quick.Directory {
		t.Fatalf("identical prelude copy should share the entry: %v", err)
	}
	license, err := os.OpenFile(filepath.Join(copy, "LICENSE.md"), os.O_APPEND|os.O_WRONLY, 0)
	if err != nil {
		t.Fatal(err)
	}
	_, _ = license.WriteString("\n")
	_ = license.Close()
	after, err := copied.Prepare(context.Background(), []string{example("quickstart")}, PrepareOptions{CacheDir: s.cache})
	if err != nil || after.Directory == before.Directory {
		t.Fatalf("prelude change did not change the cache key: %v", err)
	}
	source := s.source(t, "concurrent.jac", "shipping-cost : (Int) ->{} Int\nshipping-cost(total) = if lt(total, 50) then 5 else 0\n-- concurrent\n")
	var wait sync.WaitGroup
	programs := make([]*Program, 2)
	errs := make([]error, 2)
	for i := range programs {
		wait.Add(1)
		go func() {
			defer wait.Done()
			programs[i], errs[i] = s.runtime.Prepare(context.Background(), []string{source}, PrepareOptions{CacheDir: s.cache})
		}()
	}
	wait.Wait()
	if errs[0] != nil || errs[1] != nil || programs[0].Directory != programs[1].Directory {
		t.Fatalf("concurrent preparation: %v %v", errs[0], errs[1])
	}
	if cost, err := Call[int](context.Background(), function(t, programs[1], "shipping-cost", nil), 35); err != nil || cost != 5 {
		t.Fatalf("concurrent program: %d %v", cost, err)
	}
	entries, _ := os.ReadDir(s.cache)
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), "prepare-") {
			t.Fatalf("staging directory left behind: %s", entry.Name())
		}
	}
}

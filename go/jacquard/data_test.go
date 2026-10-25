package jacquard

import (
	"context"
	"errors"
	"fmt"
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/protocol"
)

func id(letter string) protocol.Hash { return protocol.Hash(strings.Repeat(letter, 64)) }

func numbered(n int) protocol.Hash { return protocol.Hash(fmt.Sprintf("%064x", n)) }

// fixture builds Data with synthetic identities for the builtins and a small
// Incident record, Urgency enum, and Triage union.
func fixture(t *testing.T) *Data {
	t.Helper()
	names := map[string]protocol.Hash{"int": id("1"), "real": id("2"), "text": id("3"), "bool": id("4"),
		"hash": id("5"), "list": id("6"), "option": id("7"), "incident": id("8"), "urgency": id("9")}
	constructors := map[string]protocol.Hash{"true": id("a"), "false": id("b"), "some": id("c"), "none": id("d"),
		"cons": id("e"), "nil": id("f"), "incident": id("0"), "page-now": numbered(1), "watch-only": numbered(2),
		"routed": numbered(3), "invalid-incident": numbered(4)}
	return newData(names, constructors, protocol.HardLimits)
}

type incident struct {
	ID       string
	Severity int
	Owner    *string
}

type urgency string

type triage interface{ isTriage() }
type routed struct {
	ID      string
	Urgency urgency
}
type invalidIncident struct{ ID string }

func (routed) isTriage()          {}
func (invalidIncident) isTriage() {}

func TestSignaturesAndGroups(t *testing.T) {
	names := map[string]protocol.Hash{"list": id("6"), "line": id("8"), "int": id("1"), "option": id("7"), "text": id("3"),
		"inventory": id("9"), "dispatch": id("a")}
	spec, err := parseSignature("(List Line, Int) ->{Inventory, Dispatch} Option Text", names)
	if err != nil {
		t.Fatal(err)
	}
	if len(spec.Parameters) != 2 || spec.Effects[0] != id("9") || spec.Effects[1] != id("a") {
		t.Fatalf("spec %+v", spec)
	}
	if n := spec.Result.(protocol.Nominal); n.Identity != id("7") || n.Arguments[0].(protocol.Nominal).Identity != id("3") {
		t.Fatalf("result %+v", spec.Result)
	}
	unit, err := parseSignature("() ->{} ()", names)
	if err != nil || len(unit.Result.(protocol.TupleType).Items) != 0 {
		t.Fatalf("unit %+v %v", unit, err)
	}
	for _, text := range []string{"forall a | e. (List a) ->{| e} Bool", "(Missing) ->{} Int", "(Int) ->{} Int extra)"} {
		if _, err := parseSignature(text, names); err == nil {
			t.Errorf("accepted %q", text)
		}
	}
	groups, err := hashGroups("0 " + string(id("1")) + "\n0:line " + string(id("1")) + "\n0:line " + string(id("2")) + "\nnoise\n1:other " + string(id("3")) + "\n")
	if err != nil || len(groups) != 2 || groups[0][1].Hash != id("2") {
		t.Fatalf("groups %v %v", groups, err)
	}
	if err := checkGroups([]group{{{"x", id("1")}}, {{"x", id("2")}}}); err == nil {
		t.Fatal("ambiguous name accepted")
	}
	for in, out := range map[string]string{"WorkToday": "work-today", "PageNow": "page-now", "text": "text", "Inventory": "inventory"} {
		if displayToStore(in) != out {
			t.Errorf("displayToStore(%s) = %s", in, displayToStore(in))
		}
	}
	if goName("booking-status") != "BookingStatus" || goName("app.empty?") != "AppEmpty?" {
		t.Fatal("goName")
	}
}

func TestNativeValuesRoundtripThroughCheckedTypes(t *testing.T) {
	d := fixture(t)
	if err := d.Record("Incident", incident{}); err != nil {
		t.Fatal(err)
	}
	owner := "ops"
	list := protocol.Nominal{Identity: id("6"), Arguments: []protocol.BoundaryType{protocol.Nominal{Identity: id("8")}}}
	value, err := d.Encode([]incident{{"a", 5, nil}, {"b", 2, &owner}}, list)
	if err != nil {
		t.Fatal(err)
	}
	var back []incident
	if err := d.Decode(value, list, &back); err != nil || !reflect.DeepEqual(back, []incident{{"a", 5, nil}, {"b", 2, &owner}}) {
		t.Fatalf("records: %+v %v", back, err)
	}
	real := protocol.Nominal{Identity: id("2")}
	for _, number := range []float64{1, math.Copysign(0, -1), math.Inf(1)} {
		encoded, _ := d.Encode(number, real)
		var out float64
		if err := d.Decode(encoded, real, &out); err != nil || math.Float64bits(out) != math.Float64bits(number) {
			t.Fatalf("real %v -> %v", number, out)
		}
	}
	if encoded, err := d.Encode(3, real); err != nil || encoded != protocol.RealOf(3) {
		t.Fatalf("integer at a Real parameter: %v %v", encoded, err)
	}
	integer := protocol.Nominal{Identity: id("1")}
	if _, err := d.Encode(uint64(1)<<63, integer); !isInvalid(err) {
		t.Fatalf("out-of-range uint accepted: %v", err)
	}
	if _, err := d.Encode(int64(protocol.IntMax), integer); err != nil {
		t.Fatal(err)
	}
	var small int8
	if err := d.Decode(protocol.Int(300), integer, &small); err == nil {
		t.Fatal("int8 overflow accepted")
	}
	if _, err := d.Encode("x", integer); !isInvalid(err) {
		t.Fatal("text accepted at an Int parameter")
	}
	if _, err := d.Encode("\xff", protocol.Nominal{Identity: id("3")}); !isInvalid(err) {
		t.Fatal("invalid UTF-8 accepted")
	}
}

func isInvalid(err error) bool {
	var invalid *InvalidArgument
	return errors.As(err, &invalid)
}

func TestOptionsEnumsUnionsAndDynamicValues(t *testing.T) {
	d := fixture(t)
	if err := d.Record("Routed", routed{}); err != nil {
		t.Fatal(err)
	}
	if err := d.Record("InvalidIncident", invalidIncident{}); err != nil {
		t.Fatal(err)
	}
	if err := RegisterEnum(d, map[urgency]string{"PAGE": "PageNow", "WATCH": "WatchOnly"}); err != nil {
		t.Fatal(err)
	}
	if err := RegisterUnion[triage](d, routed{}, invalidIncident{}); err != nil {
		t.Fatal(err)
	}
	encoded, err := d.Encode(routed{"r", "PAGE"}, nil)
	if err != nil {
		t.Fatal(err)
	}
	var out triage
	if err := d.Decode(encoded, nil, &out); err != nil || out != (routed{"r", "PAGE"}) {
		t.Fatalf("union: %#v %v", out, err)
	}
	if _, err := d.Encode(routed{"r", "LATER"}, nil); !isInvalid(err) {
		t.Fatal("unknown enum member accepted")
	}
	if d.Record("Routed", routed{}) == nil || d.Record("Some", struct{ X int }{}) == nil {
		t.Fatal("duplicate or builtin record accepted")
	}
	option := protocol.Nominal{Identity: id("7"), Arguments: []protocol.BoundaryType{protocol.Nominal{Identity: id("7"), Arguments: []protocol.BoundaryType{protocol.Nominal{Identity: id("1")}}}}}
	three := 3
	pointer := &three
	for _, value := range []any{nil, Some{nil}, &pointer} {
		encoded, err := d.Encode(value, option)
		if err != nil {
			t.Fatal(err)
		}
		var typed **int
		if err := d.Decode(encoded, option, &typed); err != nil {
			t.Fatal(err)
		}
		dynamic, err := d.decodeAny(encoded, option)
		if err != nil {
			t.Fatal(err)
		}
		switch value.(type) {
		case nil:
			if typed != nil || dynamic != nil {
				t.Fatalf("None: %v %v", typed, dynamic)
			}
		case Some:
			if typed == nil || *typed != nil || dynamic != (Some{nil}) {
				t.Fatalf("Some(None): %v %#v", typed, dynamic)
			}
		default:
			if typed == nil || *typed == nil || **typed != 3 || dynamic != int64(3) {
				t.Fatalf("Some(Some(3)): %v %#v", typed, dynamic)
			}
		}
	}
	variant, err := d.Make("WatchOnly")
	if err != nil {
		t.Fatal(err)
	}
	encoded, _ = d.Encode(variant, nil)
	if native, _ := d.decodeAny(encoded, nil); native != urgency("WATCH") {
		t.Fatalf("enum decodes as %#v", native)
	}
	if _, err := d.Make("Missing"); err == nil {
		t.Fatal("unknown constructor accepted")
	}
	deep := make([]any, 40)
	if _, err := d.Encode(deep, nil); !isInvalid(err) {
		t.Fatal("list deeper than the JSON depth accepted")
	}
}

type documents struct{ calls int }

func (d *documents) ReadDocument(ctx context.Context, key string) (string, error) {
	d.calls++
	if key != "example" {
		return "", host.Refused("unknown document")
	}
	return "text", ctx.Err()
}

func (d *documents) Forget(string) error { return errors.New("private detail") }

func TestServiceMethodsAdaptToHandlers(t *testing.T) {
	p := &Program{Data: fixture(t)}
	service := &documents{}
	read, ok := p.handler(service, "read-document")
	if !ok {
		t.Fatal("method not found")
	}
	value, err := read(context.Background(), []protocol.Value{protocol.Text("example")})
	if err != nil || value != protocol.Text("text") || service.calls != 1 {
		t.Fatalf("read: %v %v", value, err)
	}
	var failure *host.Failure
	if _, err := read(context.Background(), []protocol.Value{protocol.Text("other")}); !errors.As(err, &failure) || failure.Category != protocol.RefusedAuthority {
		t.Fatalf("refusal: %v", err)
	}
	if _, err := read(context.Background(), nil); !errors.As(err, &failure) || failure.Category != protocol.OutsideFailure {
		t.Fatalf("arity: %v", err)
	}
	forget, _ := p.handler(service, "forget")
	if _, err := forget(context.Background(), []protocol.Value{protocol.Text("x")}); err == nil || errors.As(err, &failure) {
		t.Fatalf("plain error should stay unclassified for the host: %v", err)
	}
	ops := Operations{"lookup-stock": func(sku string) int { return len(sku) }}
	lookup, ok := p.handler(ops, "lookup-stock")
	if !ok {
		t.Fatal("operations map entry not found")
	}
	if value, err := lookup(context.Background(), []protocol.Value{protocol.Text("abc")}); err != nil || value != protocol.Int(3) {
		t.Fatalf("lookup: %v %v", value, err)
	}
	if _, ok := p.handler(service, "missing"); ok {
		t.Fatal("missing method bound")
	}
}

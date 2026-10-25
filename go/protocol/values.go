package protocol

import (
	"fmt"
	"math"
	"regexp"
	"strconv"
)

// Limits are the v0 ceilings a host selects. Every field must lie in
// 1..HardLimits' value; the zero value is not a valid selection.
type Limits struct {
	MaxArguments        int
	MaxCollectionItems  int
	MaxDiagnosticBytes  int
	MaxDiagnostics      int
	MaxEffectRequests   int
	MaxEffects          int
	MaxFrameBytes       int
	MaxHostMessageBytes int
	MaxJSONDepth        int
	MaxOperations       int
	MaxStderrBytes      int
	MaxTextBytes        int
	MaxValueNodes       int
}

// HardLimits are the frozen v0 ceilings Core advertises in core_hello.
var HardLimits = Limits{
	MaxArguments: 64, MaxCollectionItems: 1024, MaxDiagnosticBytes: 65536,
	MaxDiagnostics: 32, MaxEffectRequests: 1024, MaxEffects: 64, MaxFrameBytes: 1048576,
	MaxHostMessageBytes: 4096, MaxJSONDepth: 64, MaxOperations: 256,
	MaxStderrBytes: 65536, MaxTextBytes: 262144, MaxValueNodes: 4096,
}

func (l *Limits) fields() []struct {
	name  string
	value *int
	hard  int
} {
	h := HardLimits
	return []struct {
		name  string
		value *int
		hard  int
	}{
		{"max_arguments", &l.MaxArguments, h.MaxArguments},
		{"max_collection_items", &l.MaxCollectionItems, h.MaxCollectionItems},
		{"max_diagnostic_bytes", &l.MaxDiagnosticBytes, h.MaxDiagnosticBytes},
		{"max_diagnostics", &l.MaxDiagnostics, h.MaxDiagnostics},
		{"max_effect_requests", &l.MaxEffectRequests, h.MaxEffectRequests},
		{"max_effects", &l.MaxEffects, h.MaxEffects},
		{"max_frame_bytes", &l.MaxFrameBytes, h.MaxFrameBytes},
		{"max_host_message_bytes", &l.MaxHostMessageBytes, h.MaxHostMessageBytes},
		{"max_json_depth", &l.MaxJSONDepth, h.MaxJSONDepth},
		{"max_operations", &l.MaxOperations, h.MaxOperations},
		{"max_stderr_bytes", &l.MaxStderrBytes, h.MaxStderrBytes},
		{"max_text_bytes", &l.MaxTextBytes, h.MaxTextBytes},
		{"max_value_nodes", &l.MaxValueNodes, h.MaxValueNodes},
	}
}

// Validate reports E1602 for a limit outside 1..its hard ceiling.
func (l Limits) Validate() error {
	for _, field := range l.fields() {
		if *field.value < 1 || *field.value > field.hard {
			return exceeded("limit %s exceeds its bounds", field.name)
		}
	}
	return nil
}

// JSON returns the wire object for host_select.
func (l Limits) JSON() Object {
	obj := Object{}
	for _, field := range l.fields() {
		obj[field.name] = int64(*field.value)
	}
	return obj
}

// DecodeLimits parses and validates an exact limits object.
func DecodeLimits(value any) (Limits, error) {
	var limits Limits
	fields := limits.fields()
	names := make([]string, len(fields))
	for i, field := range fields {
		names[i] = field.name
	}
	obj, err := exact(value, names...)
	if err != nil {
		return Limits{}, err
	}
	for _, field := range fields {
		number, ok := obj[field.name].(int64)
		if !ok || number > maxSafeInteger || number < -maxSafeInteger {
			return Limits{}, malformed("limit %s must be an integer", field.name)
		}
		if number < 1 || number > int64(field.hard) {
			return Limits{}, exceeded("limit %s exceeds its bounds", field.name)
		}
		*field.value = int(number)
	}
	return limits, nil
}

// maxSafeInteger matches the exact-integer range of the other adapters' limit checks.
const maxSafeInteger = 1<<53 - 1

var hashPattern = regexp.MustCompile(`^[0-9a-f]{64}$`)

// Hash is a HASH_V0 identity: exactly 64 lowercase hexadecimal digits.
type Hash string

// ParseHash validates a HASH_V0 identity.
func ParseHash(value any) (Hash, error) {
	text, ok := value.(string)
	if !ok || !hashPattern.MatchString(text) {
		return "", malformed("HASH_V0 must be exactly 64 lowercase hexadecimal digits")
	}
	return Hash(text), nil
}

// Value is one v0 boundary value: Int, Text, Real, Hash, Tuple, or Constructor.
type Value interface{ isValue() }

// Int is Core's signed 63-bit integer.
type Int int64

// Text is a Unicode scalar string.
type Text string

// Real preserves every binary64 bit, including NaN payloads and signed zero.
type Real struct{ Bits uint64 }

// RealOf returns the Real with the bits of a float64.
func RealOf(value float64) Real { return Real{math.Float64bits(value)} }

// Float64 returns the binary64 value. Go arithmetic may not preserve NaN payloads.
func (r Real) Float64() float64 { return math.Float64frombits(r.Bits) }

// Tuple is a positional product; the empty tuple is Jacquard unit.
type Tuple []Value

// Constructor is a nominal value: a constructor identity and its arguments.
type Constructor struct {
	Identity  Hash
	Arguments []Value
}

func (Int) isValue()         {}
func (Text) isValue()        {}
func (Real) isValue()        {}
func (Hash) isValue()        {}
func (Tuple) isValue()       {}
func (Constructor) isValue() {}

// BoundaryType is a closed v0 type: Nominal or TupleType.
type BoundaryType interface{ isType() }

// Nominal names a type identity applied to arguments.
type Nominal struct {
	Identity  Hash
	Arguments []BoundaryType
}

// TupleType is a positional product type.
type TupleType struct{ Items []BoundaryType }

func (Nominal) isType()   {}
func (TupleType) isType() {}

// Interface is the checked callable signature sent with an invocation.
type Interface struct {
	Parameters []BoundaryType
	Result     BoundaryType
	Effects    []Hash
}

// Budget counts value nodes and checks collection and text sizes for one frame.
type Budget struct {
	Limits Limits
	nodes  int
}

// NewBudget returns a fresh budget under the given limits.
func NewBudget(limits Limits) *Budget { return &Budget{Limits: limits} }

func (b *Budget) consume() error {
	b.nodes++
	if b.nodes > b.Limits.MaxValueNodes {
		return exceeded("frame exceeds max_value_nodes")
	}
	return nil
}

func (b *Budget) count(n int, arguments bool) error {
	if n > b.Limits.MaxCollectionItems || (arguments && n > b.Limits.MaxArguments) {
		return exceeded("collection exceeds selected limits")
	}
	return nil
}

func (b *Budget) text(text string) error {
	if !validScalar(text) {
		return malformed("text is not valid UTF-8")
	}
	if len(text) > b.Limits.MaxTextBytes {
		return exceeded("Text exceeds max_text_bytes")
	}
	return nil
}

// DecodeType parses one wire boundary type.
func DecodeType(value any, budget *Budget) (BoundaryType, error) {
	obj, kind, err := kinded(value)
	if err != nil {
		return nil, err
	}
	switch kind {
	case "nominal":
		if _, err := exact(obj, "arguments", "identity", "kind"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		// Core's order: exact fields, one node, identity, then the count.
		identity, err := ParseHash(obj["identity"])
		if err != nil {
			return nil, err
		}
		items, err := counted(obj["arguments"], budget, false)
		if err != nil {
			return nil, err
		}
		types, err := decodeTypeItems(items, budget)
		return Nominal{identity, types}, err
	case "tuple":
		if _, err := exact(obj, "items", "kind"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		items, err := decodeTypes(obj["items"], budget)
		return TupleType{items}, err
	}
	return nil, unsupported("unsupported boundary type kind")
}

// kinded returns a descriptor object and its kind string (E1601 when absent).
func kinded(value any) (Object, string, error) {
	obj, err := asObject(value)
	if err != nil {
		return nil, "", err
	}
	kind, ok := obj["kind"].(string)
	if !ok {
		return nil, "", malformed("a boundary descriptor needs one kind string")
	}
	return obj, kind, nil
}

func counted(value any, budget *Budget, arguments bool) ([]any, error) {
	items, err := asArray(value)
	if err != nil {
		return nil, err
	}
	return items, budget.count(len(items), arguments)
}

func decodeTypes(value any, budget *Budget) ([]BoundaryType, error) {
	items, err := counted(value, budget, false)
	if err != nil {
		return nil, err
	}
	return decodeTypeItems(items, budget)
}

func decodeTypeItems(items []any, budget *Budget) ([]BoundaryType, error) {
	var err error
	out := make([]BoundaryType, len(items))
	for i, item := range items {
		if out[i], err = DecodeType(item, budget); err != nil {
			return nil, err
		}
	}
	return out, nil
}

// EncodeType renders a boundary type, charging the budget.
func EncodeType(t BoundaryType, budget *Budget) (Object, error) { return encodeType(t, budget, 1) }

func encodeType(t BoundaryType, budget *Budget, depth int) (Object, error) {
	if depth > budget.Limits.MaxJSONDepth {
		return nil, exceeded("type nesting exceeds max_json_depth")
	}
	if err := budget.consume(); err != nil {
		return nil, err
	}
	var kind, field string
	var children []BoundaryType
	obj := Object{}
	switch v := t.(type) {
	case Nominal:
		if _, err := ParseHash(string(v.Identity)); err != nil {
			return nil, err
		}
		kind, field, children = "nominal", "arguments", v.Arguments
		obj["identity"] = string(v.Identity)
	case TupleType:
		kind, field, children = "tuple", "items", v.Items
	default:
		return nil, unsupported("unsupported boundary type")
	}
	if err := budget.count(len(children), false); err != nil {
		return nil, err
	}
	items := make([]any, len(children))
	for i, child := range children {
		item, err := encodeType(child, budget, depth+2)
		if err != nil {
			return nil, err
		}
		items[i] = item
	}
	obj["kind"], obj[field] = kind, items
	return obj, nil
}

var (
	intPattern  = regexp.MustCompile(`^(?:0|-[1-9][0-9]*|[1-9][0-9]*)$`)
	realPattern = regexp.MustCompile(`^[0-9a-f]{16}$`)
)

// DecodeValue parses one wire boundary value.
func DecodeValue(value any, budget *Budget) (Value, error) {
	obj, kind, err := kinded(value)
	if err != nil {
		return nil, err
	}
	switch kind {
	case "int":
		if _, err := exact(obj, "kind", "value"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		text, err := asString(obj["value"])
		if err != nil {
			return nil, err
		}
		if !intPattern.MatchString(text) || len(text) > 20 {
			return nil, malformed("Int must use canonical 63-bit decimal text")
		}
		number, err := strconv.ParseInt(text, 10, 64)
		if err != nil || number < IntMin || number > IntMax {
			return nil, malformed("Int is outside the 63-bit range")
		}
		return Int(number), nil
	case "real":
		if _, err := exact(obj, "bits", "kind"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		text, err := asString(obj["bits"])
		if err != nil {
			return nil, err
		}
		if !realPattern.MatchString(text) {
			return nil, malformed("Real must have exactly 16 lowercase hexadecimal digits")
		}
		bits, _ := strconv.ParseUint(text, 16, 64)
		return Real{bits}, nil
	case "text":
		if _, err := exact(obj, "kind", "value"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		text, err := asString(obj["value"])
		if err != nil {
			return nil, err
		}
		return Text(text), budget.text(text)
	case "hash":
		if _, err := exact(obj, "kind", "value"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		return ParseHash(obj["value"])
	case "tuple":
		if _, err := exact(obj, "items", "kind"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		items, err := decodeValues(obj["items"], budget, false)
		return Tuple(items), err
	case "constructor":
		if _, err := exact(obj, "arguments", "identity", "kind"); err != nil {
			return nil, err
		}
		if err := budget.consume(); err != nil {
			return nil, err
		}
		identity, err := ParseHash(obj["identity"])
		if err != nil {
			return nil, err
		}
		items, err := counted(obj["arguments"], budget, true)
		if err != nil {
			return nil, err
		}
		values, err := decodeValueItems(items, budget)
		return Constructor{identity, values}, err
	}
	return nil, unsupported("unsupported boundary value kind")
}

func decodeValues(value any, budget *Budget, arguments bool) ([]Value, error) {
	items, err := counted(value, budget, arguments)
	if err != nil {
		return nil, err
	}
	return decodeValueItems(items, budget)
}

func decodeValueItems(items []any, budget *Budget) ([]Value, error) {
	var err error
	out := make([]Value, len(items))
	for i, item := range items {
		if out[i], err = DecodeValue(item, budget); err != nil {
			return nil, err
		}
	}
	return out, nil
}

// EncodeValue renders a boundary value, charging the budget.
func EncodeValue(value Value, budget *Budget) (Object, error) { return encodeValue(value, budget, 1) }

func encodeValue(value Value, budget *Budget, depth int) (Object, error) {
	if depth > budget.Limits.MaxJSONDepth {
		return nil, exceeded("value nesting exceeds max_json_depth")
	}
	if err := budget.consume(); err != nil {
		return nil, err
	}
	switch v := value.(type) {
	case Int:
		if int64(v) < IntMin || int64(v) > IntMax {
			return nil, unsupported("Int is outside the 63-bit range")
		}
		return Object{"kind": "int", "value": strconv.FormatInt(int64(v), 10)}, nil
	case Text:
		if err := budget.text(string(v)); err != nil {
			return nil, err
		}
		return Object{"kind": "text", "value": string(v)}, nil
	case Real:
		return Object{"kind": "real", "bits": fmt.Sprintf("%016x", v.Bits)}, nil
	case Hash:
		if _, err := ParseHash(string(v)); err != nil {
			return nil, err
		}
		return Object{"kind": "hash", "value": string(v)}, nil
	case Tuple:
		items, err := encodeValues(v, budget, depth, false)
		return Object{"kind": "tuple", "items": items}, err
	case Constructor:
		if _, err := ParseHash(string(v.Identity)); err != nil {
			return nil, err
		}
		items, err := encodeValues(v.Arguments, budget, depth, true)
		return Object{"kind": "constructor", "identity": string(v.Identity), "arguments": items}, err
	}
	return nil, unsupported("value has no v0 encoding")
}

func encodeValues(values []Value, budget *Budget, depth int, arguments bool) ([]any, error) {
	if err := budget.count(len(values), arguments); err != nil {
		return nil, err
	}
	items := make([]any, len(values))
	for i, item := range values {
		encoded, err := encodeValue(item, budget, depth+2)
		if err != nil {
			return nil, err
		}
		items[i] = encoded
	}
	return items, nil
}

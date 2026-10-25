package protocol

import (
	"errors"
	"math"
	"strings"
	"testing"
)

func code(t *testing.T, err error, expected, context string) {
	t.Helper()
	var protocolError *ProtocolError
	if !errors.As(err, &protocolError) || protocolError.Code != expected {
		t.Fatalf("%s: expected %s, got %v", context, expected, err)
	}
}

func TestStrictJSONRefusals(t *testing.T) {
	for _, text := range []string{`{"a":1,"a":2}`, `{"x":{"a":1,"a":2}}`, `{"x":1.0}`, `{"x":1e0}`, `{"x":NaN}`,
		`{"x":Infinity}`, `{"x":"\ud800"}`, `{"x":"\udc00"}`, `{"x":"\ud800A"}`, `{}{}`, `[]`, `{"x":4611686018427387904}`,
		`{"x":01}`, `{"x":true,}`, `{"x":[1,]}`, "{\"x\":\"\x01\"}", `{"x":"\q"}`, `{"x":-}`} {
		_, err := DecodeObject([]byte(text), 64)
		code(t, err, "E1601", text)
	}
	_, err := DecodeObject([]byte{0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d}, 64)
	code(t, err, "E1601", "invalid UTF-8")
	obj, err := DecodeObject([]byte(`{"a":4611686018427387903,"b":-4611686018427387904,"c":"😀é"}`), 64)
	if err != nil || obj["a"] != IntMax || obj["b"] != IntMin || obj["c"] != "😀é" {
		t.Fatalf("lossless integers and escapes: %v %v", obj, err)
	}
	text, err := Stringify(Object{"z": []any{nil, true, int64(-1)}, "a": "q\"\\\n\x01😀", "__proto__": Object{}}, 64)
	if err != nil || text != `{"__proto__":{},"a":"q\"\\\n\u0001😀","z":[null,true,-1]}` {
		t.Fatalf("canonical encoding: %s %v", text, err)
	}
	_, err = Stringify(Object{"x": IntMax + 1}, 64)
	code(t, err, "E1601", "63-bit encode")
	_, err = Stringify(Object{"x": 1.5}, 64)
	code(t, err, "E1601", "float encode")
}

func TestDepthFrameAndLimits(t *testing.T) {
	_, err := DecodeObject([]byte(`{"a":[[]]}`), 2)
	code(t, err, "E1602", "decode depth")
	_, err = FrameBytes(Object{"a": "xx"}, 4, 64)
	code(t, err, "E1602", "frame bytes")
	frame, err := FrameBytes(Object{"a": int64(1)}, 64, 64)
	if err != nil || string(frame) != "\x00\x00\x00\x07{\"a\":1}" {
		t.Fatalf("frame: %q %v", frame, err)
	}
	limits := HardLimits
	limits.MaxArguments = 0
	code(t, limits.Validate(), "E1602", "zero limit")
	wire := HardLimits.JSON()
	wire["extra"] = int64(1)
	_, err = DecodeLimits(wire)
	code(t, err, "E1601", "extra limit")
	wire = HardLimits.JSON()
	wire["max_arguments"] = true
	_, err = DecodeLimits(wire)
	code(t, err, "E1601", "boolean limit")
	if decoded, err := DecodeLimits(HardLimits.JSON()); err != nil || decoded != HardLimits {
		t.Fatalf("limits roundtrip: %v %v", decoded, err)
	}
}

func TestValuesRoundtripExactly(t *testing.T) {
	values := []Value{Int(0), Int(IntMin), Int(IntMax), Text("hé😀"), Hash(strings.Repeat("a", 64)),
		Tuple{Int(3), Text("x")}, Constructor{Hash(strings.Repeat("b", 64)), []Value{Int(3)}},
		Real{0x7ff0000000000001}, RealOf(math.Copysign(0, -1)), RealOf(math.Inf(1)), Tuple{}}
	for _, value := range values {
		first, err := EncodeValue(value, NewBudget(HardLimits))
		if err != nil {
			t.Fatal(err)
		}
		decoded, err := DecodeValue(first, NewBudget(HardLimits))
		if err != nil {
			t.Fatal(err)
		}
		second, _ := EncodeValue(decoded, NewBudget(HardLimits))
		a, _ := Stringify(first, 64)
		b, _ := Stringify(second, 64)
		if a != b {
			t.Fatalf("roundtrip %v: %s != %s", value, a, b)
		}
	}
	if bits := RealOf(math.Copysign(0, -1)).Bits; bits != 0x8000000000000000 {
		t.Fatalf("signed zero bits %x", bits)
	}
	for _, text := range []string{"-0", "00", "+1", "4611686018427387904", "-4611686018427387905", " 1"} {
		_, err := DecodeValue(Object{"kind": "int", "value": text}, NewBudget(HardLimits))
		code(t, err, "E1601", text)
	}
	_, err := DecodeValue(Object{"kind": "secret", "value": "x"}, NewBudget(HardLimits))
	code(t, err, "E1604", "secret")
	_, err = EncodeValue(Int(IntMax+1), NewBudget(HardLimits))
	code(t, err, "E1604", "Int range")
	_, err = EncodeValue(Text("\xff"), NewBudget(HardLimits))
	code(t, err, "E1601", "invalid text")
	_, err = DecodeType(Object{"kind": "nominal", "identity": strings.Repeat("A", 64), "arguments": []any{}}, NewBudget(HardLimits))
	code(t, err, "E1601", "uppercase hash")
	small := HardLimits
	small.MaxValueNodes = 1
	_, err = DecodeValue(Object{"kind": "tuple", "items": []any{Object{"kind": "int", "value": "1"}}}, NewBudget(small))
	code(t, err, "E1602", "value nodes")
	_, err = DecodeValue(Object{"kind": "real", "bits": "7FF0000000000001"}, NewBudget(HardLimits))
	code(t, err, "E1601", "uppercase real")
}

func hello() Object {
	return Object{"carrier": Carrier, "kind": "core_hello", "limits": HardLimits.JSON(), "versions": []any{Protocol}}
}

func TestHelloAndSelection(t *testing.T) {
	extra := hello()
	extra["extra"] = int64(1)
	_, err := NewSession().Receive(extra)
	code(t, err, "E1601", "extra hello field")
	other := hello()
	other["versions"] = []any{"jacquard-host-v9"}
	_, err = NewSession().Receive(other)
	code(t, err, "E1600", "unknown version")
	lowered := hello()
	limits := HardLimits.JSON()
	limits["max_arguments"] = int64(1)
	lowered["limits"] = limits
	_, err = NewSession().Receive(lowered)
	code(t, err, "E1601", "lowered hello limits")
	_, err = NewSession().Receive(Object{"kind": "fatal", "protocol": Protocol})
	code(t, err, "E1608", "first frame")
	session := NewSession()
	if _, err := session.Receive(hello()); err != nil {
		t.Fatal(err)
	}
	_, err = session.Invoke(Hash(strings.Repeat("a", 64)), Interface{Result: TupleType{}}, nil, nil)
	code(t, err, "E1608", "invoke before select")
	bad := HardLimits
	bad.MaxFrameBytes = HardLimits.MaxFrameBytes + 1
	_, err = session.Select(bad)
	code(t, err, "E1602", "select above ceiling")
}

func TestInvokeFrameValidation(t *testing.T) {
	a, b := Hash(strings.Repeat("a", 64)), Hash(strings.Repeat("b", 64))
	spec := Interface{Result: TupleType{}, Effects: []Hash{b, a}}
	_, err := InvokeFrame(a, spec, nil, nil, HardLimits)
	code(t, err, "E1601", "unsorted effects")
	spec.Effects = []Hash{a}
	_, err = InvokeFrame(a, spec, nil, []OperationEntry{{b, a}}, HardLimits)
	code(t, err, "E1601", "operation outside row")
	_, err = InvokeFrame(a, spec, nil, []OperationEntry{{a, b}, {a, b}}, HardLimits)
	code(t, err, "E1601", "duplicate operation")
	_, err = InvokeFrame(a, spec, []Value{Int(1)}, nil, HardLimits)
	code(t, err, "E1601", "argument count")
	frame, err := InvokeFrame(a, spec, nil, []OperationEntry{{a, b}}, HardLimits)
	if err != nil {
		t.Fatal(err)
	}
	text, _ := Stringify(frame["capabilities"], 64)
	if text != `{"effects":["`+string(a)+`"],"operations":[{"effect":"`+string(a)+`","mode":"once","operation":"`+string(b)+`"}]}` {
		t.Fatalf("capabilities %s", text)
	}
}

func TestFailurePairs(t *testing.T) {
	for _, pair := range []struct {
		category   FailureCategory
		completion Completion
		valid      bool
	}{{UnsupportedOperation, NotStarted, true}, {RefusedAuthority, NotStarted, true}, {OutsideFailure, Failed, true},
		{CompletionUnknown, Unknown, true}, {OutsideFailure, Unknown, false}, {"secret", "", false}, {CompletionUnknown, NotStarted, false}} {
		if ValidFailure(pair.category, pair.completion) != pair.valid {
			t.Fatalf("pair %v", pair)
		}
	}
}

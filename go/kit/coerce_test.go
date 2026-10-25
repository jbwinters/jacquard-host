package kit

import (
	"testing"

	"github.com/jbwinters/jacquard-host/go/protocol"
)

func TestJSStringMatchesJavaScriptCoercion(t *testing.T) {
	for value, expected := range map[any]string{"x": "x", nil: "null", int64(5): "5", true: "true"} {
		if got := jsString(value); got != expected {
			t.Errorf("jsString(%#v) = %q, want %q", value, got, expected)
		}
	}
	if got := jsString([]any{int64(1), nil, []any{"a", "b"}}); got != "1,,a,b" {
		t.Fatalf("array: %q", got)
	}
	if got := jsString(protocol.Object{"a": int64(1)}); got != "[object Object]" {
		t.Fatalf("object: %q", got)
	}
}

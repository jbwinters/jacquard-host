package examples

import (
	"strings"
	"testing"
)

func TestPayloadIsCanonicalAndUnescaped(t *testing.T) {
	order := Order{ID: "x", Lines: []Line{{SKU: "zeta", Units: 1}, {SKU: "<a>&b", Units: 2}}}
	if got := payload(order); got != `[["<a>&b",2],["zeta",1]]` || strings.HasSuffix(got, "\n") {
		t.Fatalf("payload %q", got)
	}
}

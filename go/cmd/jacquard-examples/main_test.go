package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/jbwinters/jacquard-host/go/host"
)

func TestLiveAllFourProjectsRunAsCommands(t *testing.T) {
	if os.Getenv("JACQUARD_BIN") == "" || os.Getenv("JACQUARD_PRELUDE") == "" {
		t.Skip("live tests need JACQUARD_BIN and JACQUARD_PRELUDE")
	}
	root := t.TempDir()
	ctx := context.Background()
	command := func(arguments ...string) (string, error) {
		var out bytes.Buffer
		err := run(ctx, arguments, &out)
		return out.String(), err
	}
	for _, project := range []string{"quickstart", "planner", "triage", "fulfillment"} {
		workdir := filepath.Join(root, project)
		output, err := command("--workdir", workdir, project)
		if err != nil {
			t.Fatalf("%s: %v\n%s", project, err, output)
		}
		switch project {
		case "quickstart":
			if output != "Order total 35: shipping 5\nOrder total 75: shipping 0\n" {
				t.Fatalf("quickstart: %q", output)
			}
		case "planner":
			if !strings.Contains(output, `"score": 16`) {
				t.Fatalf("planner: %s", output)
			}
		case "triage":
			var rows []struct{ Urgency string }
			if err := json.Unmarshal([]byte(output), &rows); err != nil || len(rows) != 3 ||
				rows[0].Urgency != "PAGE" || rows[1].Urgency != "TODAY" || rows[2].Urgency != "WATCH" {
				t.Fatalf("triage: %v %s", err, output)
			}
		case "fulfillment":
			if !strings.Contains(output, `"$tag": "Fulfilled"`) {
				t.Fatalf("fulfillment: %s", output)
			}
			repeated, err := command("--workdir", workdir, project)
			if err != nil || !strings.Contains(repeated, "DuplicateOrder") || !strings.Contains(repeated, `"deliveries":1`) {
				t.Fatalf("repeat fulfillment: %v %s", err, repeated)
			}
		}
	}
	recovery := filepath.Join(root, "recovery")
	output, err := command("--workdir", recovery, "--fault", "publish-failed", "fulfillment")
	var failure *host.InvocationError
	if !errors.As(err, &failure) || failure.Result.Code() != "E1613" || !strings.Contains(output, `"pendingDeliveries":1`) {
		t.Fatalf("publish failure: %v %s", err, output)
	}
	for _, expected := range []string{`{"delivered":1}`, `{"delivered":0}`} {
		if output, err := command("--workdir", recovery, "--deliver-pending", "fulfillment"); err != nil || !strings.Contains(output, expected) {
			t.Fatalf("deliver pending: %v %s", err, output)
		}
	}
	inspect := filepath.Join(root, "triage-inspect")
	if _, err := command("--workdir", filepath.Join(root, "triage"), "--inspect", inspect, "triage"); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(inspect + ".0.json"); err != nil {
		t.Fatalf("inspection not saved: %v", err)
	}
	if _, err := command("unknown"); err == nil {
		t.Fatal("unknown project accepted")
	}
}

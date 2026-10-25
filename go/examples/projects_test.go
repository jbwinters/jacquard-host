package examples_test

import (
	"bytes"
	"context"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"testing"

	"github.com/jbwinters/jacquard-host/go/examples"
	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/jacquard"
)

func TestEmbeddedProjectsMatchTheRepositoryExamples(t *testing.T) {
	repository := filepath.Join("..", "..", "examples")
	if _, err := os.Stat(repository); err != nil {
		t.Skip("repository examples are not available outside a checkout")
	}
	count := 0
	err := fs.WalkDir(examples.Files, "data", func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() {
			return err
		}
		count++
		embedded, _ := examples.Files.ReadFile(path)
		original, err := os.ReadFile(filepath.Join(repository, path[len("data/"):]))
		if err != nil || !bytes.Equal(embedded, original) {
			t.Errorf("%s differs from examples/; copy it again", path)
		}
		return nil
	})
	if err != nil || count != 7 {
		t.Fatalf("%d embedded files, %v", count, err)
	}
}

func TestLivePartialCompletionFaultsRecoverExplicitlyAcrossRestart(t *testing.T) {
	if os.Getenv("JACQUARD_BIN") == "" || os.Getenv("JACQUARD_PRELUDE") == "" {
		t.Skip("live tests need JACQUARD_BIN and JACQUARD_PRELUDE")
	}
	root := t.TempDir()
	runtime, err := jacquard.NewRuntime(jacquard.RuntimeOptions{})
	if err != nil {
		t.Fatal(err)
	}
	rules, err := examples.Extract("fulfillment", filepath.Join(root, "sources"))
	if err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	order := examples.Order{ID: "one", Lines: []examples.Line{{SKU: "sensor", Units: 2}}}
	for _, c := range []struct {
		fault      string
		code       string
		deliveries int
	}{{"after-commit", "E1612", 1}, {"publish-failed", "E1613", 1}, {"after-delivery", "E1612", 0}} {
		program, err := runtime.Prepare(ctx, []string{rules}, jacquard.PrepareOptions{CacheDir: filepath.Join(root, "programs")})
		if err != nil {
			t.Fatal(err)
		}
		path := filepath.Join(root, c.fault, "inventory.json")
		place, warehouse, err := examples.FulfillmentProject(program, path)
		if err != nil {
			t.Fatal(err)
		}
		warehouse.Fault = c.fault
		_, err = jacquard.Call[examples.Fulfillment](ctx, place, order)
		var failure *host.InvocationError
		if !errors.As(err, &failure) || !errors.Is(err, host.ErrOperationFailed) || failure.Result.Code() != c.code {
			t.Fatalf("%s: %v", c.fault, err)
		}
		if warehouse.Summary().Bookings != 1 {
			t.Fatalf("%s: booking not retained", c.fault)
		}
		restarted, err := examples.OpenWarehouse(path, program.Data)
		if err != nil {
			t.Fatal(err)
		}
		if n, err := restarted.DeliverPending(); err != nil || n != c.deliveries {
			t.Fatalf("%s: delivered %d, %v", c.fault, n, err)
		}
		if n, _ := restarted.DeliverPending(); n != 0 {
			t.Fatalf("%s: second delivery pass delivered %d", c.fault, n)
		}
		want := examples.Summary{Bookings: 1, PendingDeliveries: 0, Deliveries: 1}
		if restarted.Summary() != want {
			t.Fatalf("%s: %+v", c.fault, restarted.Summary())
		}
		repeated, err := program.Function("fulfillment.place", jacquard.FunctionOptions{Effects: map[string]any{"Inventory": restarted, "Dispatch": restarted}})
		if err != nil {
			t.Fatal(err)
		}
		result, err := jacquard.Call[examples.Fulfillment](ctx, repeated, order)
		if _, duplicate := result.(examples.DuplicateOrder); err != nil || !duplicate || restarted.Summary() != want {
			t.Fatalf("%s: repeat %#v %v %+v", c.fault, result, err, restarted.Summary())
		}
	}
}

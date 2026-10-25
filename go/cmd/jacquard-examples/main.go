// Command jacquard-examples runs the example Jacquard projects through the Go
// SDK: quickstart, planner, triage, and fulfillment.
//
//	jacquard-examples [--workdir DIR] [--inspect PATH] [--fault NAME] [--deliver-pending] PROJECT [INPUT.json]
//
// Core is found through JACQUARD_BIN and JACQUARD_PRELUDE. The exit status is 1
// for a Jacquard or SDK failure and 2 for usage or other errors.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"slices"

	"github.com/jbwinters/jacquard-host/go/examples"
	"github.com/jbwinters/jacquard-host/go/host"
	"github.com/jbwinters/jacquard-host/go/jacquard"
)

func main() {
	if err := run(context.Background(), os.Args[1:], os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err)
		var invocation *host.InvocationError
		var preparation *jacquard.PreparationError
		var binding *jacquard.BindingError
		var argument *jacquard.InvalidArgument
		var batch *jacquard.BatchError
		if errors.As(err, &invocation) || errors.As(err, &preparation) || errors.As(err, &binding) || errors.As(err, &argument) || errors.As(err, &batch) {
			os.Exit(1)
		}
		os.Exit(2)
	}
}

func run(ctx context.Context, arguments []string, stdout io.Writer) error {
	printJSON := func(value any) error {
		encoded, err := json.MarshalIndent(value, "", "  ")
		if err != nil {
			return err
		}
		_, err = fmt.Fprintln(stdout, string(encoded))
		return err
	}
	flags := flag.NewFlagSet("jacquard-examples", flag.ContinueOnError)
	flags.SetOutput(io.Discard)
	workdir := flags.String("workdir", ".scratch/go-examples", "directory for prepared programs and example state")
	inspect := flags.String("inspect", "", "save the invocation's frames and host evidence to this path")
	fault := flags.String("fault", "", "fulfillment fault: publish-failed, after-commit, or after-delivery")
	deliverPending := flags.Bool("deliver-pending", false, "fulfillment: deliver pending outbox entries and exit")
	if err := flags.Parse(arguments); err != nil {
		return err
	}
	project, input := flags.Arg(0), flags.Arg(1)
	if !slices.Contains(examples.Projects, project) {
		return errors.New("choose quickstart, triage, planner, or fulfillment")
	}
	if !slices.Contains([]string{"", "publish-failed", "after-commit", "after-delivery"}, *fault) {
		return errors.New("unknown fault")
	}
	root, err := filepath.Abs(*workdir)
	if err != nil {
		return err
	}
	rules, err := examples.Extract(project, filepath.Join(root, "sources", project))
	if err != nil {
		return err
	}
	runtime, err := jacquard.NewRuntime(jacquard.RuntimeOptions{})
	if err != nil {
		return err
	}
	program, err := runtime.Prepare(ctx, []string{rules}, jacquard.PrepareOptions{CacheDir: filepath.Join(root, "programs")})
	if err != nil {
		return err
	}
	if project == "quickstart" {
		shipping, err := program.Function("shipping-cost", jacquard.FunctionOptions{})
		if err != nil {
			return err
		}
		for _, total := range []int{35, 75} {
			cost, err := jacquard.Call[int](ctx, shipping, total)
			if err != nil {
				return err
			}
			fmt.Fprintf(stdout, "Order total %d: shipping %d\n", total, cost)
		}
		return nil
	}
	if input == "" {
		input = filepath.Join(filepath.Dir(rules), "input.json")
	}
	raw, err := os.ReadFile(input)
	if err != nil {
		return err
	}
	save := func(invocation *jacquard.Invocation, path string) error {
		if path == "" {
			return nil
		}
		return invocation.Save(path)
	}
	switch project {
	case "planner":
		var payload struct {
			Jobs       []examples.Job `json:"jobs"`
			Capacities []int          `json:"capacities"`
		}
		if err := json.Unmarshal(raw, &payload); err != nil {
			return err
		}
		plan, err := examples.Planner(program)
		if err != nil {
			return err
		}
		invocation, err := plan.Invoke(ctx, payload.Jobs, payload.Capacities)
		if err != nil {
			return err
		}
		if err := save(invocation, *inspect); err != nil {
			return err
		}
		var result examples.Planning
		if err := invocation.Decode(&result); err != nil {
			return err
		}
		if err := printJSON(result); err != nil {
			return err
		}
		fmt.Fprintln(stdout, invocation.Render())
	case "triage":
		var payload struct {
			Policy    examples.TriagePolicy `json:"policy"`
			Incidents []examples.Incident   `json:"incidents"`
		}
		if err := json.Unmarshal(raw, &payload); err != nil {
			return err
		}
		route, err := examples.TriageProject(program)
		if err != nil {
			return err
		}
		if *inspect == "" {
			results, err := jacquard.MapBatches[examples.Triage](ctx, route, 8, payload.Policy, payload.Incidents)
			if err != nil {
				return err
			}
			return printJSON(results)
		}
		results := []examples.Triage{}
		for start := 0; start < len(payload.Incidents); start += 8 {
			invocation, err := route.Invoke(ctx, payload.Policy, payload.Incidents[start:min(start+8, len(payload.Incidents))])
			if err != nil {
				return err
			}
			if err := save(invocation, fmt.Sprintf("%s.%d.json", *inspect, start/8)); err != nil {
				return err
			}
			var batch []examples.Triage
			if err := invocation.Decode(&batch); err != nil {
				return err
			}
			results = append(results, batch...)
		}
		return printJSON(results)
	case "fulfillment":
		var order examples.Order
		if err := json.Unmarshal(raw, &order); err != nil {
			return err
		}
		place, warehouse, err := examples.FulfillmentProject(program, filepath.Join(root, "inventory.json"))
		if err != nil {
			return err
		}
		warehouse.Fault = *fault
		defer func() {
			summary, _ := json.Marshal(warehouse.Summary())
			fmt.Fprintln(stdout, string(summary))
		}()
		if *deliverPending {
			delivered, err := warehouse.DeliverPending()
			if err != nil {
				return err
			}
			encoded, _ := json.Marshal(map[string]int{"delivered": delivered})
			fmt.Fprintln(stdout, string(encoded))
			return nil
		}
		invocation, err := place.Invoke(ctx, order)
		if err != nil {
			return err
		}
		if err := save(invocation, *inspect); err != nil {
			return err
		}
		var result examples.Fulfillment
		if err := invocation.Decode(&result); err != nil {
			fmt.Fprintln(stdout, invocation.Render())
			return err
		}
		if err := printJSON(result); err != nil {
			return err
		}
		fmt.Fprintln(stdout, invocation.Render())
	}
	return nil
}

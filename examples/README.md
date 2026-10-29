# Example projects

The same Jacquard rules run through all six SDKs. The commands below use
Python; the [TypeScript walkthrough](../typescript/README.md#run-the-projects)
includes compiled Node.js front ends for all four projects, the
[Go walkthrough](../go/README.md#run-the-projects) a Go command with the rules
and inputs embedded, the [Rust walkthrough](../rust/README.md#run-the-projects)
a Cargo example that reads them from this directory, and the
[Java walkthrough](../java/README.md#run-the-projects) a Java command and the
[.NET walkthrough](../dotnet/README.md#run-the-projects) a .NET command that
read them from this directory too. Each includes an
independent fulfillment recovery example.

Start with [setup](../docs/SETUP.md), then run these from the repository root.
They use synthetic inputs, the installed Python SDK, and the pinned Core worker.
There are no additional Python dependencies or external services.

| Example | What it shows | Run |
|---|---|---|
| [Quickstart](quickstart/README.md) | One checked policy, two native calls | `python examples/quickstart/run.py` |
| [Triage](triage/README.md) | Records, enums, optional fields, independent batching | `python examples/python_sdk/app.py triage examples/triage/input.json` |
| [Planner](planner/README.md) | Bounded exhaustive optimization with typed results | `python examples/python_sdk/app.py planner examples/planner/input.json` |
| [Fulfillment](fulfillment/README.md) | Named services, SQLite, idempotency, explicit recovery | `python examples/python_sdk/app.py fulfillment examples/fulfillment/input.json` |

Each project owns its Jacquard rules and sample input. The three larger projects
share [Python mappings and the CLI](python_sdk/app.py); their
[walkthrough](python_sdk/README.md) explains the SDK calls and inspection API.
Generated stores, databases, and reports live under ignored `.scratch/` paths.
Use `--workdir` to choose isolated state for a larger project.

Run all four, including repeat fulfillment and explicit failure recovery:

```sh
python scripts/check_examples.py
```

This command requires a real Core worker. CI builds the pinned worker and runs
the same command against an installed wheel. The offline distribution check
validates packaging and recorded transcripts separately.

For correctness oracles and injected failures, run the live suite described in
[CONTRIBUTING.md](../CONTRIBUTING.md). Historical usability experiments and local
timings live in [development evidence](../docs/development/README.md).

# Triage: route incident records

After [setup](../../docs/SETUP.md), run from the repository root:

```sh
python examples/python_sdk/app.py triage examples/triage/input.json
```

Expected decisions, in input order:

| Incident | Team | Urgency | Reasons |
|---|---|---|---|
| `inc-1` | `checkout-oncall` | `PageNow` | `unowned`, `customer-impact` |
| `inc-2` | `search-team` | `WorkToday` | `ageing` |
| `inc-3` | `billing-team` | `WatchOnly` | none |

[rules.jac](rules.jac) owns the policy; [app.py](../python_sdk/app.py) maps
dataclasses, optional owners, and the `Urgency` enum. Adjust the policy or
incident fields in [input.json](input.json) to explore the decisions.

The CLI uses explicit batches of eight independent rows. Jacquard v0 encodes
lists as nested constructors, so frame depth can be reached before the item
count ceiling. Batching is safe here because rows do not affect one another.
The live suite compares a 60-row matrix with an independent Python oracle.

Add `--inspect .scratch/triage.json` to save raw protocol evidence in numbered
files, one per chunk. These files include the supplied values.

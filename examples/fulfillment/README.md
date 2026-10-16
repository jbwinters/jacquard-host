# Fulfillment: reserve inventory and recover delivery

After [setup](../../docs/SETUP.md), run from the repository root:

```sh
python examples/python_sdk/app.py fulfillment examples/fulfillment/input.json
python examples/python_sdk/app.py fulfillment examples/fulfillment/input.json
```

On a fresh work directory, the first call returns `Fulfilled`; the second
returns `DuplicateOrder` with the same receipt. State shows one booking, no
pending deliveries, and one completed delivery. Use `--workdir` for isolated
state; the default is `.scratch/python-sdk/`.

[rules.jac](rules.jac) validates the order, checks idempotency and stock,
reserves the order, and requests publication. Python's named `Inventory` and
`Dispatch` services in [app.py](../python_sdk/app.py) own SQLite. A booking and
its outbox entry commit in one transaction. A separate SQLite table simulates
a durable delivery sink that deduplicates receipts.

Try a failed dispatch in a new work directory:

```sh
python examples/python_sdk/app.py fulfillment examples/fulfillment/input.json \
  --workdir .scratch/fulfillment-failure --fault publish-failed
```

This intentionally exits 1 with E1613: one booking, one pending delivery, zero
completed deliveries. Repeating the order recognizes its booking without
automatically retrying delivery. Reconcile explicitly:

```sh
python examples/python_sdk/app.py fulfillment \
  --workdir .scratch/fulfillment-failure --deliver-pending
python examples/python_sdk/app.py fulfillment \
  --workdir .scratch/fulfillment-failure --deliver-pending
```

The first reconciliation reports `delivered: 1`; the second reports
`delivered: 0`. Use a new work directory for each fault experiment. The
`after-commit` and `after-delivery` faults return E1612 (unknown completion)
on either side of the simulated sink accepting the receipt. The live suite
checks all three fault paths and persistence across restarts.

This example's recovery guarantee depends on its SQLite sink and receipt
contract. A real message queue needs its own delivery and reconciliation
contract. See the [SDK walkthrough](../python_sdk/README.md) for binding,
native values, and inspection details.

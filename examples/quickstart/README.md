# Quickstart: a shipping policy

After [setup](../../docs/SETUP.md), run from the repository root:

```sh
python examples/quickstart/run.py
```

Expected output:

```text
Order total 35: shipping 5
Order total 75: shipping 0
```

[rules.jac](rules.jac) owns the free-shipping threshold.
[run.py](run.py) prepares it and calls the checked function with Python integers.
Preparation caches live under `.scratch/jacquard-host/`; a repeat reuses the
verified store. Each call runs a fresh worker. Try changing the threshold in the
rules and running again: the source change creates a new cache entry.

Next, try [triage](../triage/README.md) for records and batching or
[fulfillment](../fulfillment/README.md) for host services.

# Planner: choose work under capacity and deadlines

After [setup](../../docs/SETUP.md), run from the repository root:

```sh
python examples/python_sdk/app.py planner examples/planner/input.json
```

The result is `Planned`, with score **16**: `fix-a` and `fix-b` occupy slot 1;
`docs` occupies slot 2. Their combined value beats choosing `large` first.
Assignment order is not significant.

[rules.jac](rules.jac) enumerates the feasible assignments and maximizes value;
Python maps the native `Job`, `Plan`, and `Assignment` records in
[app.py](../python_sdk/app.py). No host effect or external solver is involved.

Change capacities, efforts, values, and deadlines in [input.json](input.json).
The search deliberately accepts at most six jobs and one to three slots;
invalid inputs return `InvalidPlan`. This is a bounded decision example, not
a scalable production scheduling algorithm. Global optimization must run as
one decision; independent batching would change its meaning.

The live suite compares 25 problems with a separate exhaustive Python oracle
and checks returned assignments against their capacities and deadlines.

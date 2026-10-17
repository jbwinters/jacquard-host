import { Runtime, type Infer } from 'jacquard-host';
const program = await new Runtime({ prelude: '/configured/prelude' }).prepare('rules.jac');
const d = program.data;
const shipping = program.function('shipping-cost', { parameters: [d.int], result: d.int });
const cost: number = await shipping.call(35);
void cost;
// @ts-expect-error checked application codec accepts a number
shipping.call('35');
// @ts-expect-error argument count is part of the bound function type
shipping.call(35, 75);
// @ts-expect-error return codec preserves its type
const wrong: string = await shipping.call(35);
const Incident = d.record('Incident', { id: d.text, severity: d.int, owner: d.option(d.text) });
type Incident = Infer<typeof Incident>;
const incident: Incident = Incident.create({ id: 'one', severity: 5, owner: null });
// @ts-expect-error bool is not an Int field
Incident.create({ id: 'one', severity: true, owner: null });
// @ts-expect-error missing declared field
Incident.create({ id: 'one', severity: 5 });
const Good = d.record('Good', { id: d.text });
const Bad = d.record('Bad', { reason: d.text });
const decide = program.function('decide', { parameters: [Incident], result: d.union(Good, Bad) });
const result = await decide.call(incident);
if (result.$tag === 'Good') {
  const id: string = result.id; void id;
  // @ts-expect-error discriminated union narrows the fields
  result.reason;
} else {
  const reason: string = result.reason; void reason;
}
const batch = program.function('batch', { parameters: [d.list(Incident)], result: d.list(Good) });
const records = await batch.mapBatches([[incident]], { batchSize: 8 });
const id: string = records[0].id; void id;

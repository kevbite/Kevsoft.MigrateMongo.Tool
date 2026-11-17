// @ts-check
import { collection, view } from '@kevsoft/migrate-mongo';

export const orders = collection('orders', c => {
  c.indexes
    .add({ customerId: 1, createdAt: -1 })
    .add({ reference: 1 }, { unique: true, name: 'reference_unique' })
    .add({ expiresAt: 1 }, { expireAfter: '30d' });
  c.validator.jsonSchema({ bsonType: 'object', required: ['customerId', 'reference'] }).level('moderate');
});

export const openOrders = view('open_orders', { on: 'orders' }, v =>
  v.pipeline([{ $match: { status: 'open' } }, { $project: { reference: 1, customerId: 1 } }]));
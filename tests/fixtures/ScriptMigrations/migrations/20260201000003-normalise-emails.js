// @ts-check
import { defineMigration } from '@kevsoft/migrate-mongo';
import { normalise } from '../lib/emails.js';

export default defineMigration({
  async up(db, context) {
    const users = db.collection('users', { label: 'normalise' });
    const operations = [];
    for await (const user of users.find({}, { sort: { _id: 1 } })) {
      const email = normalise(user.email);
      if (email !== user.email) {
        operations.push({ updateOne: { filter: { _id: user._id }, update: { $set: { email, previousEmail: user.email } } } });
      }
    }

    const result = await users.bulkWrite(operations);
    context.results.set('normalised', result.modifiedCount);
    context.results.note(`Checked ${await users.countDocuments()} users.`);
    console.log('normalised', result.modifiedCount, 'of', await users.estimatedDocumentCount());
  },

  async down(db, context) {
    const result = await db.collection('users').updateMany(
      { previousEmail: { $exists: true } },
      [{ $set: { email: '$previousEmail' } }, { $unset: 'previousEmail' }],
    );
    context.results.set('restored', result.modifiedCount);
  },
});
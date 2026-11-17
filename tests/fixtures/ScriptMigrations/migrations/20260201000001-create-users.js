// Collection creation can't run in a transaction, so this one opts out.
export const transaction = 'never';

export async function up(db) {
  await db.createCollection('users');
  await db.collection('users').createIndex({ email: 1 }, { unique: true, name: 'email_unique' });
}

export async function down(db) {
  await db.dropCollection('users');
}
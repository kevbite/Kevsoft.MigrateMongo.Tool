// CommonJS, as written for the Node migrate-mongo tool.
module.exports = {
  async up(db) {
    await db.collection('roles').insertMany([
      { _id: 'admin', permissions: ['read', 'write'] },
      { _id: 'reader', permissions: ['read'] },
    ]);
    await db.collection('users').insertMany([
      { _id: 'ann', email: 'ANN@Example.com', roles: ['admin'] },
      { _id: 'bob', email: 'bob@example.com ', roles: ['reader'] },
      { _id: 'cat', email: 'cat@example.com', roles: ['reader'] },
    ]);
  },

  async down(db) {
    await db.collection('users').deleteMany({ _id: { $in: ['ann', 'bob', 'cat'] } });
    await db.collection('roles').deleteMany({});
  },
};
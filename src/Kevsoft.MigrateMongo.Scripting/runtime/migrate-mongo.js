// The '@kevsoft/migrate-mongo' module. The host sets globalThis.__mm to a flat object of native (C#) functions; everything
// shaped for users (db, collections, cursors, builders) is plain JavaScript here, so the C# surface stays small.
const n = globalThis.__mm;

// ---- BSON types ----

export function ObjectId(hex) { return n.objectId(hex === undefined || hex === null ? null : String(hex)); }
ObjectId.isValid = value => typeof value === 'string' && /^[0-9a-fA-F]{24}$/.test(value);
ObjectId.createFromTime = seconds => n.objectIdFromTime(Number(seconds));

export function Decimal128(value) { return n.decimal128(String(value)); }
Decimal128.fromString = value => n.decimal128(String(value));

export function Long(value) { return n.long(String(value)); }
Long.fromNumber = value => n.long(String(Math.trunc(value)));
Long.fromString = value => n.long(String(value));

export function UUID(value) { return n.uuid(value === undefined || value === null ? null : String(value)); }

export function Timestamp({ t, i }) { return n.timestamp(Number(t), Number(i)); }

// ---- Durations ----

const units = { ms: 0.001, s: 1, m: 60, h: 3600, d: 86400, w: 604800 };

/** Seconds from a number of seconds or a string such as '30d', '12h', '15m', '45s' or '1d12h'. */
function seconds(value, what) {
  if (typeof value === 'number' && Number.isFinite(value) && value >= 0) return value;
  const text = String(value);
  const parts = [...text.matchAll(/(\d+(?:\.\d+)?)(ms|s|m|h|d|w)/g)];
  if (typeof value !== 'string' || parts.length === 0 || parts.map(p => p[0]).join('') !== text) {
    throw new TypeError(`${what}: '${text}' isn't a duration. Use seconds, or a string such as '30d', '12h', '15m', '45s' or '1d12h'.`);
  }
  return parts.reduce((total, [, amount, unit]) => total + Number(amount) * units[unit], 0);
}

// ---- Database ----

function track(options) {
  if (options === undefined) return undefined;
  const { label, captureStatement, ...rest } = options;
  const unknown = Object.keys(rest);
  if (unknown.length > 0) throw new TypeError(`Unknown collection option(s): ${unknown.join(', ')}. Use label or captureStatement.`);
  return { label, captureStatement };
}

class Cursor {
  #open;
  #handle;
  #buffer = [];
  #done = false;
  #closed = false;

  constructor(open) { this.#open = open; }

  async #fill() {
    while (this.#buffer.length === 0 && !this.#done) {
      if (this.#handle === undefined) {
        if (this.#closed) throw new Error('The cursor is closed.');
        this.#handle = await this.#open(this._options());
      }
      const batch = await n.nextBatch(this.#handle);
      if (batch === null) {
        this.#done = true;
      } else {
        this.#buffer.push(...batch);
      }
    }
  }

  _options() { return {}; }

  _ensureNotStarted() {
    if (this.#handle !== undefined || this.#closed) throw new Error('The cursor has already started; set options before reading from it.');
  }

  async hasNext() { await this.#fill(); return this.#buffer.length > 0; }
  async next() { await this.#fill(); return this.#buffer.length > 0 ? this.#buffer.shift() : null; }
  async tryNext() { return this.next(); }

  async toArray() {
    const all = [];
    for await (const doc of this) all.push(doc);
    return all;
  }

  async forEach(callback) {
    for await (const doc of this) {
      if (await callback(doc) === false) break;
    }
  }

  async close() {
    this.#closed = true;
    this.#done = true;
    this.#buffer = [];
    if (this.#handle !== undefined) n.closeCursor(this.#handle);
  }

  async *[Symbol.asyncIterator]() {
    try {
      while (true) {
        const doc = await this.next();
        if (doc === null) return;
        yield doc;
      }
    } finally {
      await this.close();
    }
  }
}

class FindCursor extends Cursor {
  #options;
  constructor(open, options) { super(open); this.#options = { ...options }; }
  _options() { return this.#options; }
  #set(name, value) { this._ensureNotStarted(); this.#options[name] = value; return this; }
  sort(sort) { return this.#set('sort', sort); }
  skip(skip) { return this.#set('skip', skip); }
  limit(limit) { return this.#set('limit', limit); }
  project(projection) { return this.#set('projection', projection); }
  batchSize(size) { return this.#set('batchSize', size); }
}

class AggregationCursor extends Cursor {
  #options;
  constructor(open, options) { super(open); this.#options = { ...options }; }
  _options() { return this.#options; }
  batchSize(size) { this._ensureNotStarted(); this.#options.batchSize = size; return this; }
}

class Collection {
  #name;
  #track;

  constructor(name, options) {
    if (typeof name !== 'string' || name.length === 0) throw new TypeError('A collection name is required.');
    this.#name = name;
    this.#track = track(options);
  }

  get collectionName() { return this.#name; }
  get dbName() { return n.databaseName; }

  insertOne(document) { return n.write('insertOne', this.#name, this.#track, document); }
  insertMany(documents, options = {}) { return n.write('insertMany', this.#name, this.#track, documents, options); }
  updateOne(filter, update, options = {}) { return n.write('updateOne', this.#name, this.#track, filter, update, options); }
  updateMany(filter, update, options = {}) { return n.write('updateMany', this.#name, this.#track, filter, update, options); }
  replaceOne(filter, replacement, options = {}) { return n.write('replaceOne', this.#name, this.#track, filter, replacement, options); }
  deleteOne(filter = {}, options = {}) { return n.write('deleteOne', this.#name, this.#track, filter, options); }
  deleteMany(filter = {}, options = {}) { return n.write('deleteMany', this.#name, this.#track, filter, options); }
  bulkWrite(operations, options = {}) { return n.write('bulkWrite', this.#name, this.#track, operations, options); }

  find(filter = {}, options = {}) { return new FindCursor(o => n.openCursor('find', this.#name, filter, o), options); }

  async findOne(filter = {}, options = {}) {
    const cursor = this.find(filter, { ...options, limit: 1 });
    const document = await cursor.next();
    await cursor.close();
    return document;
  }

  aggregate(pipeline = [], options = {}) { return new AggregationCursor(o => n.openCursor('aggregate', this.#name, pipeline, o), options); }
  countDocuments(filter = {}, options = {}) { return n.count(this.#name, filter, options); }
  estimatedDocumentCount() { return n.estimatedCount(this.#name); }
  distinct(field, filter = {}) { return n.distinct(this.#name, field, filter); }

  createIndex(keys, options = {}) { return n.write('createIndex', this.#name, this.#track, keys, options); }
  async dropIndex(name) { await n.write('dropIndex', this.#name, this.#track, name); return true; }
  async dropIndexes() { await n.write('dropIndexes', this.#name, this.#track); return true; }
  listIndexes() { return new Cursor(() => n.openCursor('listIndexes', this.#name)); }

  async indexExists(name) {
    const names = Array.isArray(name) ? name : [name];
    const existing = (await this.listIndexes().toArray()).map(i => i.name);
    return names.every(x => existing.includes(x));
  }

  drop() { return dropCollection(this.#name, this.#track); }

  async rename(newName, options = {}) {
    await n.adminCommand({
      renameCollection: `${n.databaseName}.${this.#name}`,
      to: `${n.databaseName}.${newName}`,
      ...(options.dropTarget ? { dropTarget: true } : {}),
    }, this.#track);
    return new Collection(newName);
  }
}

async function dropCollection(name, tracking) {
  try {
    await n.command({ drop: name }, tracking);
    return true;
  } catch (e) {
    if (e.code === 26) return false; // NamespaceNotFound
    throw e;
  }
}

export function createDb() {
  return Object.freeze({
    get databaseName() { return n.databaseName; },
    collection: (name, options) => new Collection(name, options),
    command: (command, options) => n.command(command, track(options)),
    async createCollection(name, options = {}) {
      await n.command({ create: name, ...options });
      return new Collection(name);
    },
    dropCollection: name => dropCollection(name),
    renameCollection: (from, to, options = {}) => new Collection(from).rename(to, options),
    listCollections(filter = {}, options = {}) { return new Cursor(() => n.openCursor('listCollections', null, filter, options)); },
  });
}

// ---- Migrations ----

/** Identity function, so editors infer the types of `up`, `down` and `transaction`. */
export function defineMigration(migration) { return migration; }

// ---- Collection definitions: fluent builders mirroring the C# CollectionBuilder ----

const indexOptions = new Set([
  'name', 'unique', 'sparse', 'hidden', 'expireAfterSeconds', 'expireAfter', 'partialFilterExpression', 'collation', 'weights',
  'default_language', 'language_override', 'textIndexVersion', '2dsphereIndexVersion', 'bits', 'min', 'max',
  'wildcardProjection', 'storageEngine', 'onRebuild',
]);

function check(value, allowed, what) {
  if (value !== undefined && !allowed.includes(value)) {
    throw new TypeError(`${what} must be ${allowed.map(a => `'${a}'`).join(' or ')}, not '${value}'.`);
  }
  return value;
}

class IndexesBuilder {
  #declared = [];
  #ignored = [];
  #prune;
  #onRebuild;
  get prune() { return this.#prune; }
  set prune(value) { this.#prune = check(value, ['report', 'hide', 'drop'], 'indexes.prune'); }
  get onRebuild() { return this.#onRebuild; }
  set onRebuild(value) { this.#onRebuild = check(value, ['allow', 'fail'], 'indexes.onRebuild'); }

  add(keys, options = {}) {
    if (keys === null || typeof keys !== 'object' || Array.isArray(keys) || Object.keys(keys).length === 0) {
      throw new TypeError('indexes.add needs the index keys, e.g. { email: 1 }.');
    }
    const unknown = Object.keys(options).filter(k => !indexOptions.has(k));
    if (unknown.length > 0) throw new TypeError(`Unknown index option(s): ${unknown.join(', ')}.`);
    const { expireAfter, onRebuild, ...rest } = options;
    if (expireAfter !== undefined) {
      if (rest.expireAfterSeconds !== undefined) throw new TypeError('Set expireAfter or expireAfterSeconds, not both.');
      rest.expireAfterSeconds = seconds(expireAfter, 'expireAfter');
    }
    this.#declared.push({ keys, options: rest, onRebuild: check(onRebuild, ['allow', 'fail'], 'onRebuild') });
    return this;
  }

  ignore(pattern) { this.#ignored.push(String(pattern)); return this; }
  _build() { return { declared: this.#declared, ignored: this.#ignored, prune: this.#prune, onRebuild: this.#onRebuild }; }
}

class SearchIndexesBuilder {
  #declared = [];
  #ignored = [];
  #prune;
  get prune() { return this.#prune; }
  set prune(value) { this.#prune = check(value, ['report', 'drop'], 'searchIndexes.prune'); }
  add(name, definition) { this.#declared.push({ name, type: 'search', definition }); return this; }
  addVector(name, definition) { this.#declared.push({ name, type: 'vectorSearch', definition }); return this; }
  ignore(pattern) { this.#ignored.push(String(pattern)); return this; }
  _build() { return { declared: this.#declared, ignored: this.#ignored, prune: this.#prune }; }
}

class OptionsBuilder {
  #options = {};
  #onUnchangeable;
  #onConvertToCapped;
  get onUnchangeable() { return this.#onUnchangeable; }
  set onUnchangeable(value) { this.#onUnchangeable = check(value, ['fail', 'skip'], 'options.onUnchangeable'); }
  get onConvertToCapped() { return this.#onConvertToCapped; }
  set onConvertToCapped(value) { this.#onConvertToCapped = check(value, ['allow', 'fail'], 'options.onConvertToCapped'); }

  capped({ maxSize, maxDocuments } = {}) { this.#options.capped = { maxSize, maxDocuments }; return this; }

  timeSeries(time, { meta, granularity, bucketMaxSpan, bucketRounding } = {}) {
    check(granularity, ['seconds', 'minutes', 'hours'], 'granularity');
    this.#options.timeSeries = {
      time,
      meta,
      granularity,
      bucketMaxSpan: bucketMaxSpan === undefined ? undefined : seconds(bucketMaxSpan, 'bucketMaxSpan'),
      bucketRounding: bucketRounding === undefined ? undefined : seconds(bucketRounding, 'bucketRounding'),
    };
    return this;
  }

  expireAfter(value) { this.#options.expireAfter = seconds(value, 'expireAfter'); return this; }

  clustered({ name, expireAfter } = {}) {
    this.#options.clustered = { name, expireAfter: expireAfter === undefined ? undefined : seconds(expireAfter, 'expireAfter') };
    return this;
  }

  collation(collation) { this.#options.collation = collation; return this; }
  changeStreamPreAndPostImages(enabled = true) { this.#options.changeStreamPreAndPostImages = Boolean(enabled); return this; }
  _build() { return { ...this.#options, onUnchangeable: this.#onUnchangeable, onConvertToCapped: this.#onConvertToCapped }; }
}

class ValidatorBuilder {
  #schema;
  #level;
  #action;
  jsonSchema(schema) { this.#schema = schema; return this; }
  level(level) { this.#level = check(level, ['strict', 'moderate', 'constraint', 'off'], 'validator.level'); return this; }
  action(action) { this.#action = check(action, ['error', 'warn', 'errorAndLog'], 'validator.action'); return this; }
  _build() { return { jsonSchema: this.#schema, level: this.#level, action: this.#action }; }
}

const definitionMarker = Symbol.for('@kevsoft/migrate-mongo/definition');

export function collection(name, define) {
  if (typeof name !== 'string' || name.length === 0) throw new TypeError('collection() needs a name.');
  const c = Object.freeze({
    name,
    indexes: new IndexesBuilder(),
    searchIndexes: new SearchIndexesBuilder(),
    options: new OptionsBuilder(),
    validator: new ValidatorBuilder(),
  });
  if (define !== undefined) define(c);
  return {
    [definitionMarker]: true,
    kind: 'collection',
    name,
    indexes: c.indexes._build(),
    searchIndexes: c.searchIndexes._build(),
    options: c.options._build(),
    validator: c.validator._build(),
  };
}

export function view(name, { on } = {}, define) {
  if (typeof name !== 'string' || name.length === 0) throw new TypeError('view() needs a name.');
  if (typeof on !== 'string' || on.length === 0) {
    throw new TypeError(`view('${name}') needs the collection or view it reads from: view('${name}', { on: 'collection' }, v => ...).`);
  }
  let pipeline;
  let collation;
  const v = Object.freeze({
    name,
    on,
    pipeline(stages) { pipeline = stages; return v; },
    collation(value) { collation = value; return v; },
  });
  if (define !== undefined) define(v);
  return { [definitionMarker]: true, kind: 'view', name, on, pipeline, collation };
}

/** Used by the host to tell definitions apart from other exports. */
export function isDefinition(value) { return value !== null && typeof value === 'object' && value[definitionMarker] === true; }

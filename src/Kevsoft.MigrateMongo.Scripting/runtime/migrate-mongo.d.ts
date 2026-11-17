// Types for scripts run by Kevsoft.MigrateMongo (https://github.com/kevbite/Kevsoft.MigrateMongo.Tool).
// Documents are open objects with field names as they're stored, the JavaScript equivalent of a BsonDocument.
// This file is written by `migrate-mongo init --js` and kept up to date by `migrate-mongo create`; don't edit it.

/** Writes to the migration's log. */
interface Console {
  log(...data: any[]): void;
  info(...data: any[]): void;
  debug(...data: any[]): void;
  warn(...data: any[]): void;
  error(...data: any[]): void;
}
declare var console: Console;

/** CommonJS migrations (module.exports = { up, down }) can require the runtime module, and nothing else. */
declare function require(id: '@kevsoft/migrate-mongo'): typeof import('@kevsoft/migrate-mongo');
declare var module: { exports: any };
declare var exports: any;

declare module '@kevsoft/migrate-mongo' {
  /** A MongoDB document, filter, update, projection or pipeline stage: field names as they're stored. */
  export type Document = Record<string, any>;

  /** A duration: seconds, or a string such as '30d', '1d12h', '90m', '500ms' or '2w'. */
  export type Duration = number | string;

  export interface ObjectId {
    toHexString(): string;
    toString(): string;
    toJSON(): string;
    getTimestamp(): Date;
    equals(other: unknown): boolean;
  }
  export const ObjectId: {
    (hex?: string): ObjectId;
    new (hex?: string): ObjectId;
    isValid(hex: unknown): boolean;
    createFromTime(seconds: number): ObjectId;
  };

  export interface Decimal128 { toString(): string; toJSON(): string; toNumber(): number; equals(other: unknown): boolean }
  export const Decimal128: {
    (value: string): Decimal128;
    new (value: string): Decimal128;
    fromString(value: string): Decimal128;
  };

  /** A 64-bit integer kept as one when stored. Numbers read back from the database are plain numbers. */
  export interface Long { toString(): string; toJSON(): string; toNumber(): number; equals(other: unknown): boolean }
  export const Long: {
    (value: number | bigint | string): Long;
    new (value: number | bigint | string): Long;
    fromNumber(value: number): Long;
    fromString(value: string): Long;
  };

  export interface UUID { toString(): string; toJSON(): string; equals(other: unknown): boolean }
  export const UUID: { (value?: string): UUID; new (value?: string): UUID };

  export interface Timestamp { toString(): string; toJSON(): string; equals(other: unknown): boolean }
  export const Timestamp: { (value: { t: number; i: number }): Timestamp; new (value: { t: number; i: number }): Timestamp };

  export interface TrackingOptions {
    /** Shown against the operations in `show`. */
    label?: string;
    /** Set to false to leave the statements out of the history (e.g. they hold sensitive data). */
    captureStatement?: boolean;
  }

  export interface InsertOneResult { acknowledged: boolean; insertedId: any }
  export interface InsertManyResult { acknowledged: boolean; insertedCount: number; insertedIds: Record<number, any> }
  export interface UpdateResult { acknowledged: boolean; matchedCount: number; modifiedCount: number; upsertedCount: number; upsertedId: any }
  export interface DeleteResult { acknowledged: boolean; deletedCount: number }
  export interface BulkWriteResult {
    acknowledged: boolean;
    insertedCount: number;
    matchedCount: number;
    modifiedCount: number;
    deletedCount: number;
    upsertedCount: number;
  }

  export interface WriteOptions { hint?: string | Document; collation?: Document }
  export interface UpdateOptions extends WriteOptions { upsert?: boolean; arrayFilters?: Document[] }
  export interface FindOptions {
    sort?: Document;
    projection?: Document;
    skip?: number;
    limit?: number;
    batchSize?: number;
    hint?: string | Document;
    collation?: Document;
  }
  export interface AggregateOptions {
    allowDiskUse?: boolean;
    batchSize?: number;
    hint?: string | Document;
    collation?: Document;
    let?: Document;
  }
  export type AnyBulkWriteOperation =
    | { insertOne: { document: Document } }
    | { updateOne: { filter: Document; update: Document | Document[]; upsert?: boolean; arrayFilters?: Document[]; hint?: string | Document; collation?: Document } }
    | { updateMany: { filter: Document; update: Document | Document[]; upsert?: boolean; arrayFilters?: Document[]; hint?: string | Document; collation?: Document } }
    | { replaceOne: { filter: Document; replacement: Document; upsert?: boolean; hint?: string | Document; collation?: Document } }
    | { deleteOne: { filter: Document; hint?: string | Document; collation?: Document } }
    | { deleteMany: { filter: Document; hint?: string | Document; collation?: Document } };

  export interface Cursor<T = Document> extends AsyncIterable<T> {
    hasNext(): Promise<boolean>;
    next(): Promise<T | null>;
    tryNext(): Promise<T | null>;
    toArray(): Promise<T[]>;
    forEach(callback: (document: T) => unknown): Promise<void>;
    close(): Promise<void>;
  }
  export interface FindCursor<T = Document> extends Cursor<T> {
    sort(sort: Document): this;
    skip(skip: number): this;
    limit(limit: number): this;
    project(projection: Document): this;
    batchSize(size: number): this;
  }
  export interface AggregationCursor<T = Document> extends Cursor<T> {}

  export interface Collection {
    readonly collectionName: string;
    insertOne(document: Document): Promise<InsertOneResult>;
    insertMany(documents: Document[], options?: { ordered?: boolean }): Promise<InsertManyResult>;
    updateOne(filter: Document, update: Document | Document[], options?: UpdateOptions): Promise<UpdateResult>;
    updateMany(filter: Document, update: Document | Document[], options?: UpdateOptions): Promise<UpdateResult>;
    replaceOne(filter: Document, replacement: Document, options?: WriteOptions & { upsert?: boolean }): Promise<UpdateResult>;
    deleteOne(filter: Document, options?: WriteOptions): Promise<DeleteResult>;
    deleteMany(filter: Document, options?: WriteOptions): Promise<DeleteResult>;
    bulkWrite(operations: AnyBulkWriteOperation[], options?: { ordered?: boolean }): Promise<BulkWriteResult>;
    find(filter?: Document, options?: FindOptions): FindCursor;
    findOne(filter?: Document, options?: FindOptions): Promise<Document | null>;
    aggregate(pipeline: Document[], options?: AggregateOptions): AggregationCursor;
    countDocuments(filter?: Document, options?: { limit?: number; skip?: number; hint?: string | Document; collation?: Document }): Promise<number>;
    estimatedDocumentCount(): Promise<number>;
    distinct(field: string, filter?: Document): Promise<any[]>;
    createIndex(keys: Document, options?: Document & { name?: string }): Promise<string>;
    dropIndex(name: string): Promise<void>;
    dropIndexes(): Promise<void>;
    listIndexes(): Cursor;
    indexExists(name: string | string[]): Promise<boolean>;
    /** Resolves false when the collection doesn't exist. */
    drop(): Promise<boolean>;
    rename(newName: string, options?: { dropTarget?: boolean }): Promise<Collection>;
  }

  export interface Db {
    readonly databaseName: string;
    /** Every write through the collection is recorded in the migration's history, shown by `show`. */
    collection(name: string, options?: TrackingOptions): Collection;
    command(command: Document, options?: TrackingOptions): Promise<Document>;
    createCollection(name: string, options?: Document): Promise<Collection>;
    /** Resolves false when the collection doesn't exist. */
    dropCollection(name: string): Promise<boolean>;
    renameCollection(from: string, to: string, options?: { dropTarget?: boolean }): Promise<Collection>;
    listCollections(filter?: Document): Cursor;
  }

  export interface Logger {
    log(...data: any[]): void;
    info(...data: any[]): void;
    debug(...data: any[]): void;
    warn(...data: any[]): void;
    error(...data: any[]): void;
  }

  export interface MigrationResults {
    /** Records a value against the migration, shown by `show`. */
    set(key: string, value: unknown): void;
    get(key: string): any;
    increment(key: string, by?: number): number;
    remove(key: string): boolean;
    note(message: string, level?: 'info' | 'warning'): void;
  }

  export interface MigrationContext {
    readonly results: MigrationResults;
    readonly logger: Logger;
    readonly run: {
      readonly id: string;
      readonly batch: number;
      readonly direction: 'up' | 'down';
      readonly transactional: boolean;
      readonly environment: string | null;
    };
    readonly migration: { readonly id: string; readonly name: string };
  }

  export type TransactionMode = 'auto' | 'always' | 'never';

  export interface Migration {
    up(db: Db, context: MigrationContext): Promise<void> | void;
    down?(db: Db, context: MigrationContext): Promise<void> | void;
    /** Whether the migration runs in a transaction; 'auto' (the default) uses one when the server supports it. */
    transaction?: TransactionMode;
  }

  /** Returns the migration as it is; it's only there so editors know the types of `db` and `context`. */
  export function defineMigration<T extends Migration>(migration: T): T;

  // ---- Collection and view definitions ----

  export interface IndexOptions {
    name?: string;
    unique?: boolean;
    sparse?: boolean;
    hidden?: boolean;
    expireAfterSeconds?: number;
    expireAfter?: Duration;
    partialFilterExpression?: Document;
    collation?: Document;
    weights?: Document;
    default_language?: string;
    language_override?: string;
    textIndexVersion?: number;
    '2dsphereIndexVersion'?: number;
    bits?: number;
    min?: number;
    max?: number;
    wildcardProjection?: Document;
    storageEngine?: Document;
    /** Whether a change to this index that needs it dropping and creating again is allowed. */
    onRebuild?: 'allow' | 'fail';
  }

  export interface IndexesBuilder {
    add(keys: Document, options?: IndexOptions): IndexesBuilder;
    /** Indexes whose name matches (`*` wildcards allowed) are left alone. */
    ignore(pattern: string): IndexesBuilder;
    /** What to do with indexes on the server that aren't declared. Defaults to 'report'. */
    prune: 'report' | 'hide' | 'drop';
    onRebuild: 'allow' | 'fail';
  }

  export interface SearchIndexesBuilder {
    add(name: string, definition: Document): SearchIndexesBuilder;
    addVector(name: string, definition: Document): SearchIndexesBuilder;
    ignore(pattern: string): SearchIndexesBuilder;
    prune: 'report' | 'drop';
  }

  export interface CollectionOptionsBuilder {
    capped(options: { maxSize: number; maxDocuments?: number }): CollectionOptionsBuilder;
    timeSeries(
      timeField: string,
      options?: {
        meta?: string;
        granularity?: 'seconds' | 'minutes' | 'hours';
        bucketMaxSpan?: Duration;
        bucketRounding?: Duration;
      },
    ): CollectionOptionsBuilder;
    expireAfter(duration: Duration): CollectionOptionsBuilder;
    clustered(options?: { name?: string; expireAfter?: Duration }): CollectionOptionsBuilder;
    collation(collation: Document): CollectionOptionsBuilder;
    changeStreamPreAndPostImages(enabled?: boolean): CollectionOptionsBuilder;
    /** What to do when an option can't be changed on an existing collection. */
    onUnchangeable: 'fail' | 'skip';
    onConvertToCapped: 'allow' | 'fail';
  }

  export interface ValidatorBuilder {
    jsonSchema(schema: Document): ValidatorBuilder;
    level(level: 'strict' | 'moderate' | 'constraint' | 'off'): ValidatorBuilder;
    action(action: 'error' | 'warn' | 'errorAndLog'): ValidatorBuilder;
  }

  export interface CollectionBuilder {
    readonly indexes: IndexesBuilder;
    readonly searchIndexes: SearchIndexesBuilder;
    readonly options: CollectionOptionsBuilder;
    readonly validator: ValidatorBuilder;
  }

  export interface ViewBuilder {
    pipeline(stages: Document[]): ViewBuilder;
    collation(collation: Document): ViewBuilder;
  }

  export interface CollectionDefinition { readonly kind: 'collection'; readonly name: string }
  export interface ViewDefinition { readonly kind: 'view'; readonly name: string; readonly on: string }
  export type Definition = CollectionDefinition | ViewDefinition;

  /** Declares how a collection should be; `apply` makes the database match. */
  export function collection(name: string, define?: (c: CollectionBuilder) => void): CollectionDefinition;
  /** Declares a view on a collection (or another view). */
  export function view(name: string, options: { on: string }, define: (v: ViewBuilder) => void): ViewDefinition;
  export function isDefinition(value: unknown): value is Definition;
}

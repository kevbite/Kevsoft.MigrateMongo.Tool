using Kevsoft.MigrateMongo.Scripting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Scripting;

public sealed class ScriptMigrationSourceTests : IDisposable
{
    private static readonly IMongoDatabase Database = new MongoClient("mongodb://localhost:27017").GetDatabase("scripts");
    private readonly ScriptFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Migrations_are_read_from_named_exports_define_migration_and_common_js()
    {
        _folder
            .Write("migrations/20260101000000-add-users.js", """
                export async function up(db) {}
                export async function down(db) {}
                export const transaction = 'never';
                """)
            .Write("migrations/20260102000000_backfill_orders.js", """
                import { defineMigration } from '@kevsoft/migrate-mongo';
                export default defineMigration({ transaction: 'always', async up(db, context) {} });
                """)
            .Write("migrations/20260103000000-ported.js", """
                const { defineMigration } = require('@kevsoft/migrate-mongo');
                module.exports = { async up(db) {}, async down(db) {} };
                """)
            .Write("migrations/README.md", "not a script");

        var migrations = new ScriptMigrationSource(_folder.Root).GetMigrations();

        migrations.Select(m => (m.Id, m.Name, m.Source, m.Origin, m.Transaction)).ShouldBe([
            ("20260101000000", "add-users", "js", "migrations/20260101000000-add-users.js", TransactionMode.Never),
            ("20260102000000", "backfill_orders", "js", "migrations/20260102000000_backfill_orders.js", TransactionMode.Always),
            ("20260103000000", "ported", "js", "migrations/20260103000000-ported.js", TransactionMode.Auto),
        ]);
    }

    [Fact]
    public void No_folders_means_no_migrations_or_collections()
    {
        var source = new ScriptMigrationSource(_folder.Root);

        source.GetMigrations().ShouldBeEmpty();
        source.GetCollections().ShouldBeEmpty();
    }

    [Fact]
    public void The_checksum_ignores_whitespace_and_comments_but_not_code()
    {
        static string Checksum(string code)
        {
            using var folder = new ScriptFolder().Write("migrations/1-a.js", code);
            return new ScriptMigrationSource(folder.Root).GetMigrations().Single().Checksum;
        }

        var original = Checksum("export async function up(db) { await db.collection('a').deleteMany({}); }");

        original.ShouldStartWith("sha256:");
        Checksum("""
            // Clears out a
            export async function up(db) {
                /* everything */
                await db.collection('a').deleteMany({});
            }
            """).ShouldBe(original);
        Checksum("export async function up(db) { await db.collection('b').deleteMany({}); }").ShouldNotBe(original);
    }

    [Fact]
    public void Helpers_can_be_imported_from_inside_the_root()
    {
        _folder
            .Write("lib/names.js", "export const transaction = 'never';")
            .Write("migrations/1-uses-helper.js", """
                import { transaction as mode } from '../lib/names.js';
                export const transaction = mode;
                export async function up() {}
                """);

        new ScriptMigrationSource(_folder.Root).GetMigrations().Single().Transaction.ShouldBe(TransactionMode.Never);
    }

    [Theory]
    [InlineData("migrations/add-users.js", "export async function up() {}", "migrations/add-users.js: a migration's file name must be its id")]
    [InlineData("migrations/1-no-up.js", "export async function down() {}", "migrations/1-no-up.js doesn't export an up function")]
    [InlineData("migrations/1-bad-down.js", "export async function up() {} export const down = 1;", "migrations/1-bad-down.js: down must be a function.")]
    [InlineData("migrations/1-bad-mode.js", "export async function up() {} export const transaction = 'sometimes';",
        "migrations/1-bad-mode.js: transaction must be 'auto', 'always' or 'never', not sometimes.")]
    [InlineData("migrations/1-syntax.js", "export async function up( {", "migrations/1-syntax.js:1:")]
    [InlineData("migrations/1-throws.js", "throw new TypeError('not today'); export async function up() {}", "TypeError: not today")]
    public void Invalid_migrations_are_reported_with_their_file(string file, string code, string message)
    {
        _folder.Write(file, code);

        var error = Should.Throw<ScriptException>(() => new ScriptMigrationSource(_folder.Root).GetMigrations());

        error.Message.ShouldContain(message);
    }

    [Fact]
    public void Scripts_cant_import_files_outside_the_root()
    {
        using var outside = new ScriptFolder().Write("secret.js", "export const transaction = 'never';");
        _folder.Write("migrations/1-escape.js", $$"""
            import { transaction } from '{{new Uri(Path.Combine(outside.Root, "secret.js")).AbsoluteUri}}';
            export async function up() {}
            """);

        Should.Throw<ScriptException>(() => new ScriptMigrationSource(_folder.Root).GetMigrations());
    }

    [Fact]
    public void Collection_definitions_render_like_the_csharp_builders()
    {
        _folder.Write("collections/orders.js", """
            import { collection } from '@kevsoft/migrate-mongo';

            export default collection('orders', c => {
              c.indexes.prune = 'hide';
              c.indexes.onRebuild = 'fail';
              c.indexes
                .add({ reference: 1 }, { name: 'reference_unique', unique: true })
                .add({ closedAt: 1 }, { expireAfter: '30d', onRebuild: 'allow' })
                .add({ companyId: 1, status: -1 }, { partialFilterExpression: { status: 'open' }, collation: { locale: 'en', strength: 2 } })
                .add({ title: 'text' }, { weights: { title: 5 }, default_language: 'french' })
                .ignore('adhoc_*');
              c.searchIndexes.prune = 'drop';
              c.searchIndexes
                .add('default', { mappings: { dynamic: true } })
                .addVector('embeddings', { fields: [{ type: 'vector', path: 'embedding', numDimensions: 3, similarity: 'cosine' }] })
                .ignore('legacy');
              c.options.changeStreamPreAndPostImages();
              c.options.onUnchangeable = 'skip';
              c.validator
                .jsonSchema({ required: ['reference'], properties: { reference: { bsonType: 'string' } } })
                .level('moderate')
                .action('errorAndLog');
            });
            """);

        var descriptor = new ScriptMigrationSource(_folder.Root).GetCollections().ShouldHaveSingleItem();
        var declaration = descriptor.Declare(null!, Database);

        (descriptor.Name, descriptor.Source, descriptor.Origin, descriptor.IsView).ShouldBe(("orders", "js", "collections/orders.js", false));
        declaration.Prune.ShouldBe(IndexPrune.Hide);
        declaration.OnRebuild.ShouldBe(IndexRebuild.Fail);
        declaration.Ignored.ShouldBe(["adhoc_*"]);
        declaration.Indexes.Select(i => (i.Keys, i.Options, i.OnRebuild)).ShouldBe([
            (new BsonDocument("reference", 1), new BsonDocument { { "name", "reference_unique" }, { "unique", true } }, null),
            (new BsonDocument("closedAt", 1), new BsonDocument("expireAfterSeconds", 2592000L), IndexRebuild.Allow),
            (
                new BsonDocument { { "companyId", 1 }, { "status", -1 } },
                new BsonDocument
                {
                    { "collation", new BsonDocument { { "locale", "en" }, { "strength", 2 } } },
                    { "partialFilterExpression", new BsonDocument("status", "open") },
                },
                (IndexRebuild?)null),
            (new BsonDocument("title", "text"), new BsonDocument { { "weights", new BsonDocument("title", 5) }, { "default_language", "french" } }, null),
        ]);
        declaration.SearchPrune.ShouldBe(SearchIndexPrune.Drop);
        declaration.SearchIgnored.ShouldBe(["legacy"]);
        declaration.SearchIndexes.Select(i => (i.Name, i.Type, i.Definition)).ShouldBe([
            ("default", "search", BsonDocument.Parse("{ mappings: { dynamic: true } }")),
            ("embeddings", "vectorSearch", BsonDocument.Parse("{ fields: [{ type: 'vector', path: 'embedding', numDimensions: 3, similarity: 'cosine' }] }")),
        ]);
        declaration.Options.ShouldBe(BsonDocument.Parse("{ changeStreamPreAndPostImages: { enabled: true } }"));
        declaration.OnUnchangeable.ShouldBe(UnchangeableOption.Skip);
        var validator = declaration.Validator.ShouldNotBeNull();
        validator.Validator.ShouldBe(BsonDocument.Parse("{ $jsonSchema: { required: ['reference'], properties: { reference: { bsonType: 'string' } } } }"));
        (validator.Level, validator.Action).ShouldBe(("moderate", "errorAndLog"));
    }

    [Fact]
    public void Collection_options_take_durations_as_strings_or_seconds()
    {
        _folder
            .Write("collections/metrics.js", """
                import { collection } from '@kevsoft/migrate-mongo';
                export default collection('metrics', c => c.options
                  .timeSeries('at', { meta: 'sensor', bucketMaxSpan: '1h', bucketRounding: 3600 })
                  .expireAfter('1d12h'));
                """)
            .Write("collections/events.js", """
                import { collection } from '@kevsoft/migrate-mongo';
                export default collection('events', c => c.options.capped({ maxSize: 1048576, maxDocuments: 1000 }));
                """)
            .Write("collections/sessions.js", """
                import { collection } from '@kevsoft/migrate-mongo';
                export default collection('sessions', c => {
                  c.options.clustered({ expireAfter: '2w' }).collation({ locale: 'en' });
                  c.options.onConvertToCapped = 'fail';
                });
                """);

        var declarations = new ScriptMigrationSource(_folder.Root).GetCollections().ToDictionary(c => c.Name, c => c.Declare(null!, Database));

        declarations["metrics"].Options.ShouldBe(BsonDocument.Parse(
            "{ timeseries: { timeField: 'at', metaField: 'sensor', bucketMaxSpanSeconds: 3600, bucketRoundingSeconds: 3600 }, expireAfterSeconds: NumberLong(129600) }"));
        declarations["events"].Options.ShouldBe(BsonDocument.Parse("{ capped: true, size: NumberLong(1048576), max: NumberLong(1000) }"));
        declarations["sessions"].Options.ShouldBe(BsonDocument.Parse(
            "{ clusteredIndex: { key: { _id: 1 }, unique: true }, expireAfterSeconds: NumberLong(1209600), collation: { locale: 'en' } }"));
        declarations["sessions"].OnConvertToCapped.ShouldBe(ConvertToCapped.Fail);
    }

    [Fact]
    public void A_file_can_export_several_definitions_including_views()
    {
        _folder.Write("collections/customers.js", """
            import { collection, view } from '@kevsoft/migrate-mongo';

            export const customers = collection('customers', c => c.indexes.add({ email: 1 }, { unique: true }));
            export default [
              view('activeCustomers', { on: 'customers' }, v => v
                .pipeline([{ $match: { active: true } }, { $project: { email: 1 } }])
                .collation({ locale: 'en', strength: 2 })),
            ];
            export const notADefinition = 42;
            """);

        var descriptors = new ScriptMigrationSource(_folder.Root).GetCollections();

        descriptors.Select(d => (d.Name, d.ViewOn)).ShouldBe([("customers", null), ("activeCustomers", "customers")], ignoreOrder: true);
        var view = descriptors.Single(d => d.IsView).Declare(null!, Database).View.ShouldNotBeNull();
        view.ViewOn.ShouldBe("customers");
        view.Pipeline.ShouldBe([BsonDocument.Parse("{ $match: { active: true } }"), BsonDocument.Parse("{ $project: { email: 1 } }")]);
        view.Collation.ShouldBe(BsonDocument.Parse("{ locale: 'en', strength: 2 }"));
    }

    [Theory]
    [InlineData("export const x = 1;", "collections/bad.js doesn't export a collection or view definition")]
    [InlineData("import { collection } from '@kevsoft/migrate-mongo'; export default collection('a', c => { c.indexes.prune = 'sometimes'; });",
        "indexes.prune must be 'report' or 'hide' or 'drop', not 'sometimes'.")]
    [InlineData("import { collection } from '@kevsoft/migrate-mongo'; export default collection('a', c => c.indexes.add({ a: 1 }, { uniq: true }));",
        "Unknown index option(s): uniq.")]
    [InlineData("import { collection } from '@kevsoft/migrate-mongo'; export default collection('a', c => c.options.expireAfter('soon'));",
        "expireAfter")]
    [InlineData("import { view } from '@kevsoft/migrate-mongo'; export default view('v', {}, v => v);", "view('v') needs the collection or view it reads from")]
    public void Invalid_definitions_are_reported_with_their_file(string code, string message)
    {
        _folder.Write("collections/bad.js", code);

        var error = Should.Throw<ScriptException>(() => new ScriptMigrationSource(_folder.Root).GetCollections());

        error.Message.ShouldContain(message);
        error.Message.ShouldContain("collections/bad.js");
    }

    [Fact]
    public void Time_series_bucketing_needs_both_spans()
    {
        _folder.Write("collections/metrics.js", """
            import { collection } from '@kevsoft/migrate-mongo';
            export default collection('metrics', c => c.options.timeSeries('at', { bucketMaxSpan: '1h' }));
            """);

        var descriptor = new ScriptMigrationSource(_folder.Root).GetCollections().Single();

        Should.Throw<ScriptException>(() => descriptor.Declare(null!, Database)).Message
            .ShouldBe("collections/metrics.js: collection 'metrics': timeSeries takes a granularity, or both bucketMaxSpan and bucketRounding.");
    }
}

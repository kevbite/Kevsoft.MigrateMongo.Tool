using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>What <c>context.Track(...)</c> records for the indexes a migration or a collection definition creates.</summary>
public sealed class TrackedIndexTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Created_indexes_are_recorded_with_their_options_as_createIndexes_names_them()
    {
        await using var mongo = new CollectionHarness(environment);
        var results = new MigrationResults();
        var collection = mongo.Context(results).Track(mongo.Collection());

        await collection.CreateIndexAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Text("title"),
            new CreateIndexOptions<BsonDocument> { Name = "search", Unique = false, DefaultLanguage = "french", LanguageOverride = "lang", TextIndexVersion = 3 }));
        await collection.CreateIndexAsync(new BsonDocument { { "key", new BsonDocument("code", 1) }, { "name", "by_code" }, { "sparse", true } });

        results.Operations.Select(o => (o.Kind, o.Statement)).ShouldBe([
            ("createIndex", new BsonDocument
            {
                { "keys", new BsonDocument("title", "text") },
                { "options", new BsonDocument { { "name", "search" }, { "default_language", "french" }, { "language_override", "lang" }, { "textIndexVersion", 3 } } },
            }),
            ("createIndex", new BsonDocument
            {
                { "keys", new BsonDocument("code", 1) },
                { "options", new BsonDocument { { "name", "by_code" }, { "sparse", true } } },
            }),
        ]);
        mongo.Indexes().Keys.ShouldBe(["_id_", "search", "by_code"], ignoreOrder: true);
    }
}

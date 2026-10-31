using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Abstractions;

public sealed class TrackedCollectionTests
{
    private static MigrationContext Context(MigrationResults results, IMongoDatabase? database = null) =>
        new(database!, null, NullLogger.Instance, null!, new MigrationRunInfo("run-1", 1, MigrationDirection.Up, false, null), results);

    /// <summary>An <c>orders</c> collection whose operations all fail with <paramref name="failure"/>.</summary>
    private static IMongoCollection<BsonDocument> FailingOrders(Exception failure) =>
        DriverFake.Of<IMongoCollection<BsonDocument>>(method => method.Name switch
        {
            "get_CollectionNamespace" => new CollectionNamespace("shop", "orders"),
            "get_DocumentSerializer" => BsonDocumentSerializer.Instance,
            "get_Settings" => new MongoCollectionSettings(),
            _ when typeof(Task).IsAssignableFrom(method.ReturnType) => DriverFake.Faulted(method.ReturnType, failure),
            _ => throw new NotSupportedException(method.Name),
        });

    [Fact]
    public async Task Failed_operations_are_recorded_with_their_error_and_rethrown()
    {
        var results = new MigrationResults();
        var failure = new MongoException("not primary");
        var orders = Context(results).Track(FailingOrders(failure), "archive");

        var thrown = await Should.ThrowAsync<MongoException>(() => orders.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("status", "complete"),
            Builders<BsonDocument>.Update.Set("archived", true)));

        thrown.ShouldBeSameAs(failure);
        var operation = results.Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe("updateMany");
        operation.Collection.ShouldBe("orders");
        operation.Error.ShouldBe("MongoException: not primary");
        operation.Counts.ShouldBeEmpty();
        operation.Statement.ShouldBe(BsonDocument.Parse("""{ "filter": { "status": "complete" }, "update": { "$set": { "archived": true } } }"""));
        results["archive"].ShouldBeNull();
    }

    [Fact]
    public async Task Failed_insert_many_records_what_was_inserted_before_the_error()
    {
        var results = new MigrationResults();
        var documents = new[] { new BsonDocument("_id", 1), new BsonDocument("_id", 1), new BsonDocument("_id", 2) };
        var applied = new BulkWriteResult<BsonDocument>.Acknowledged(
            requestCount: 3, matchedCount: 0, deletedCount: 0, insertedCount: 2, modifiedCount: 0,
            processedRequests: documents.Select(d => (WriteModel<BsonDocument>)new InsertOneModel<BsonDocument>(d)).ToList(), upserts: []);
        var failure = new MongoBulkWriteException<BsonDocument>(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))), applied, [], null, []);
        var orders = Context(results).Track(FailingOrders(failure), "seeded");

        await Should.ThrowAsync<MongoBulkWriteException<BsonDocument>>(() => orders.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }));

        var operation = results.Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe("insertMany");
        operation.Counts.ShouldBe(new Dictionary<string, long> { ["inserted"] = 2 });
        operation.Error.ShouldNotBeNull().ShouldStartWith("MongoBulkWriteException: ");
        results["seeded"].ShouldBe(new BsonDocument("inserted", 2L));
    }

    [Fact]
    public async Task Dropping_indexes_in_a_transaction_fails_before_reaching_the_server()
    {
        var results = new MigrationResults();
        var session = DriverFake.Of<IClientSessionHandle>(method => method.Name == "get_IsInTransaction" ? true : throw new NotSupportedException(method.Name));
        var context = new MigrationContext(null!, session, NullLogger.Instance, null!, new MigrationRunInfo("run-1", 1, MigrationDirection.Up, true, null), results);
        var orders = context.Track(DriverFake.Of<IMongoCollection<BsonDocument>>(method => method.Name == "get_CollectionNamespace"
            ? new CollectionNamespace("shop", "orders")
            : throw new NotSupportedException(method.Name)));

        var byName = await Should.ThrowAsync<InvalidOperationException>(() => orders.DropIndexAsync("status_1"));
        await Should.ThrowAsync<InvalidOperationException>(() => orders.DropIndexAsync(Builders<BsonDocument>.IndexKeys.Ascending("status")));
        await Should.ThrowAsync<InvalidOperationException>(() => orders.DropAllIndexesAsync());

        byName.Message.ShouldBe(
            "Indexes on 'orders' can't be dropped in a transaction: MongoDB doesn't allow dropIndexes in a multi-document " +
            "transaction. Run the migration without one: [Migration(..., Transaction = TransactionMode.Never)].");
        results.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_commands_are_recorded_with_their_error_and_rethrown()
    {
        var results = new MigrationResults();
        var failure = new MongoException("collMod failed");
        var database = DriverFake.Of<IMongoDatabase>(method => method.Name switch
        {
            nameof(IMongoDatabase.RunCommandAsync) => DriverFake.Faulted(method.ReturnType, failure),
            _ => throw new NotSupportedException(method.Name),
        });

        await Should.ThrowAsync<MongoException>(() => Context(results).Track(database).RunCommandAsync(new BsonDocument("collMod", "orders")));

        var operation = results.Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe("runCommand");
        operation.Collection.ShouldBe("orders");
        operation.Error.ShouldBe("MongoException: collMod failed");
        operation.Statement.ShouldBe(new BsonDocument("command", new BsonDocument("collMod", "orders")));
    }
}

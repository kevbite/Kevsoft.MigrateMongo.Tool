using System.Net;
using Kevsoft.MigrateMongo.MongoDriver;
using Kevsoft.MigrateMongo.Tests.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class MongoMigrationExecutorTests
{
    [Theory]
    [InlineData(true, "{ setName: 'rs0' }", true)]
    [InlineData(true, "{ }", false)]
    // MongoDB 4.2 before 4.2.10 and 4.4 before 4.4.2 don't have `hello`.
    [InlineData(false, "{ setName: 'rs0' }", true)]
    [InlineData(false, "{ msg: 'isdbgrid' }", true)]
    [InlineData(false, "{ }", false)]
    public async Task Replica_sets_and_sharded_clusters_support_transactions(bool hasHello, string reply, bool supportsTransactions)
    {
        var commands = new List<string>();
        var database = DriverFake.Of<IMongoDatabase>((_, args) =>
        {
            var command = ((BsonDocumentCommand<BsonDocument>)args[0]!).Document;
            commands.Add(command.GetElement(0).Name);
            return command.Contains("hello") && !hasHello
                ? Task.FromException<BsonDocument>(new MongoCommandException(
                    new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
                    "Command hello failed: no such command: 'hello'.",
                    command,
                    BsonDocument.Parse("{ ok: 0, errmsg: \"no such command: 'hello'\", code: 59, codeName: 'CommandNotFound' }")))
                : Task.FromResult(BsonDocument.Parse(reply));
        });
        var executor = new MongoMigrationExecutor(null!, database, null!, NullLoggerFactory.Instance);

        var supported = await executor.SupportsTransactionsAsync(TestContext.Current.CancellationToken);

        supported.ShouldBe(supportsTransactions);
        commands.ShouldBe(hasHello ? ["hello"] : ["hello", "isMaster"]);
    }
}

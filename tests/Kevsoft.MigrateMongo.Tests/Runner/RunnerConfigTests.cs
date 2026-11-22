using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Protocol;
using Kevsoft.MigrateMongo.Tests.Engine;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Runner;

public sealed class RunnerConfigTests
{
    [Theory]
    [InlineData("FAIL", "warn", "Config setting 'indexes.onRebuild' must be allow or fail.")]
    [InlineData("allow", "FAIL", "Config setting 'validators.onInvalidDocuments' must be warn or fail.")]
    public async Task Invalid_policy_returns_failure_before_connecting(string rebuild, string invalidDocuments, string message)
    {
        var config = new ResolvedConfig
        {
            MongoUrl = "mongodb://[",
            DatabaseName = "app",
            IndexesOnRebuild = rebuild,
            ValidatorsOnInvalidDocuments = invalidDocuments,
        };

        var result = await MigrateMongoRunner.ExecuteAsync("up", new CommandArguments(), config, null,
            new MigrateMongoRunnerOptions(), null, new RecordingSink(), CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message.ShouldBe(message);
    }
}

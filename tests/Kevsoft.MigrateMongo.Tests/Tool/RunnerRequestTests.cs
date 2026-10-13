using System.Text.Json;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tool;

public sealed class RunnerRequestTests
{
    [Fact]
    public void Dry_run_is_sent_to_the_runner()
    {
        var request = new RunnerRequest { Command = "up", Arguments = new CommandArguments { DryRun = true } };

        JsonSerializer.Serialize(request, ProtocolJsonContext.Default.RunnerRequest).ShouldContain("\"dryRun\":true");
    }
}

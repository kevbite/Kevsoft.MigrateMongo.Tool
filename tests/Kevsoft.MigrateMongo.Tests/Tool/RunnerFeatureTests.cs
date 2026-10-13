using System.Text.Json;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Protocol;
using Kevsoft.MigrateMongo.Tool;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tool;

public sealed class RunnerFeatureTests
{
    private static CliInvocation Up(bool dryRun) => new() { Command = "up", Arguments = new CommandArguments { DryRun = dryRun } };

    [Fact]
    public void Dry_run_is_refused_for_a_runner_that_does_not_advertise_it()
    {
        // What a 0.1.x runner prints for --mm-handshake: no "features".
        var handshake = JsonSerializer.Deserialize(
            """{"protocol":[1],"library":"0.1.1","driver":"3.12.0","runtime":"8.0.20"}""",
            ProtocolJsonContext.Default.HandshakeInfo)!;

        var error = Should.Throw<ToolException>(() => ToolCommands.EnsureRunnerSupports(Up(dryRun: true), handshake));

        error.ExitCode.ShouldBe(5);
        error.Message.ShouldBe(
            "--dry-run isn't supported by the runner (Kevsoft.MigrateMongo 0.1.1); nothing was run. " +
            "Update the Kevsoft.MigrateMongo.Runner package in your migrations project to 0.2.0 or later.");
    }

    [Fact]
    public void Dry_run_is_refused_when_features_do_not_include_it()
    {
        var handshake = new HandshakeInfo { Protocol = [1], Library = "9.9.9", Features = ["somethingElse"] };

        Should.Throw<ToolException>(() => ToolCommands.EnsureRunnerSupports(Up(dryRun: true), handshake));
    }

    [Fact]
    public void Dry_run_is_allowed_for_a_runner_that_advertises_it()
    {
        var handshake = JsonSerializer.Deserialize(
            """{"protocol":[1],"features":["dryRun"],"library":"0.2.0","driver":"3.12.0","runtime":"8.0.20"}""",
            ProtocolJsonContext.Default.HandshakeInfo)!;

        Should.NotThrow(() => ToolCommands.EnsureRunnerSupports(Up(dryRun: true), handshake));
    }

    [Fact]
    public void Old_runners_still_work_for_commands_without_new_features()
    {
        var handshake = new HandshakeInfo { Protocol = [1], Library = "0.1.1" };

        Should.NotThrow(() => ToolCommands.EnsureRunnerSupports(Up(dryRun: false), handshake));
    }

    [Fact]
    public void Dry_run_is_sent_to_the_runner()
    {
        var request = new RunnerRequest { Command = "up", Arguments = new CommandArguments { DryRun = true } };

        JsonSerializer.Serialize(request, ProtocolJsonContext.Default.RunnerRequest).ShouldContain("\"dryRun\":true");
    }
}

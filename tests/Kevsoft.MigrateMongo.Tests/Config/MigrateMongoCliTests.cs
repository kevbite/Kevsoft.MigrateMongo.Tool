using Kevsoft.MigrateMongo.Cli;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Config;

public sealed class MigrateMongoCliTests
{
    private static async Task<(int ExitCode, CliInvocation? Invocation)> Parse(bool includeToolCommands, params string[] args)
    {
        CliInvocation? captured = null;
        var root = MigrateMongoCli.Create("test", includeToolCommands, (invocation, _) =>
        {
            captured = invocation;
            return Task.FromResult(0);
        });
        var parse = root.Parse(args);
        var exitCode = await parse.InvokeAsync(new System.CommandLine.InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null });
        return (exitCode, captured);
    }

    [Theory]
    [InlineData("status", "--no-build")]
    [InlineData("--no-build", "status")]
    [InlineData("up", "--no-build")]
    [InlineData("down", "--no-build", "--count", "1")]
    public async Task Tool_accepts_no_build_before_or_after_the_command(params string[] args)
    {
        var (exitCode, invocation) = await Parse(true, args);

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.NoBuild.ShouldBeTrue();
    }

    [Fact]
    public async Task Tool_accepts_insecure_skip_host_key_check()
    {
        var (_, invocation) = await Parse(true, "up", "--insecure-skip-host-key-check");

        invocation.ShouldNotBeNull();
        invocation.Command.ShouldBe("up");
        invocation.InsecureSkipHostKeyCheck.ShouldBeTrue();
    }

    [Theory]
    [InlineData("status", "--no-build")]
    [InlineData("up", "--insecure-skip-host-key-check")]
    public async Task Standalone_runner_rejects_tool_only_options(params string[] args)
    {
        var (exitCode, invocation) = await Parse(false, args);

        exitCode.ShouldNotBe(0);
        invocation.ShouldBeNull();
    }
}

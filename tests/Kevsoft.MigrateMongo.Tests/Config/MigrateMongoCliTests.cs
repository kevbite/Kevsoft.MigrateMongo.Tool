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
    [InlineData(true, "up", "--dry-run")]
    [InlineData(false, "up", "--dry-run", "--to", "20260101000002")]
    [InlineData(true, "down", "--batch", "--dry-run")]
    [InlineData(false, "down", "--dry-run")]
    public async Task Up_and_down_accept_dry_run_in_the_tool_and_standalone_runner(bool includeToolCommands, params string[] args)
    {
        var (exitCode, invocation) = await Parse(includeToolCommands, args);

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.Command.ShouldBe(args[0]);
        invocation.Arguments.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task Up_is_not_a_dry_run_by_default()
    {
        var (_, invocation) = await Parse(true, "up");

        invocation.ShouldNotBeNull();
        invocation.Arguments.DryRun.ShouldBeFalse();
    }

    [Theory]
    [InlineData("plan", "--dry-run")]
    [InlineData("baseline", "--to", "20260101000001", "--dry-run")]
    public async Task Dry_run_is_rejected_by_other_commands(params string[] args)
    {
        var (exitCode, invocation) = await Parse(true, args);

        exitCode.ShouldNotBe(0);
        invocation.ShouldBeNull();
    }

    [Theory]
    [InlineData("status", "--no-build")]
    [InlineData("up", "--insecure-skip-host-key-check")]
    [InlineData("bundle")]
    public async Task Standalone_runner_rejects_tool_only_options(params string[] args)
    {
        var (exitCode, invocation) = await Parse(false, args);

        exitCode.ShouldNotBe(0);
        invocation.ShouldBeNull();
    }

    [Fact]
    public async Task Bundle_defaults_to_the_bundle_directory_and_current_runtime()
    {
        var (exitCode, invocation) = await Parse(true, "bundle");

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.Command.ShouldBe("bundle");
        invocation.Output.ShouldBe("bundle");
        invocation.RuntimeIdentifier.ShouldBeNull();
    }

    [Fact]
    public async Task Bundle_accepts_runtime_output_and_config_file()
    {
        var (exitCode, invocation) = await Parse(true, "bundle", "-r", "linux-x64", "-o", "out/migrations", "-f", "config.yaml");

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.RuntimeIdentifier.ShouldBe("linux-x64");
        invocation.Output.ShouldBe("out/migrations");
        invocation.ConfigFile.ShouldBe("config.yaml");
    }
}

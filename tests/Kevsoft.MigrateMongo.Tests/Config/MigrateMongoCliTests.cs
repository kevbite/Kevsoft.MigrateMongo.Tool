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

    [Theory]
    [InlineData(false, "create", "AddProducts")]
    [InlineData(true, "create", "AddProducts", "--minimal")]
    public async Task Create_accepts_minimal(bool minimal, params string[] args)
    {
        var (exitCode, invocation) = await Parse(true, args);

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.Name.ShouldBe("AddProducts");
        invocation.Minimal.ShouldBe(minimal);
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
    [InlineData("0")]
    [InlineData("-5")]
    public async Task History_rejects_a_limit_below_one(string limit)
    {
        var (exitCode, invocation) = await Parse(true, "history", "--limit", limit);

        exitCode.ShouldNotBe(0);
        invocation.ShouldBeNull();
    }

    [Fact]
    public async Task History_accepts_a_positive_limit()
    {
        var (exitCode, invocation) = await Parse(true, "history", "--limit", "5");

        exitCode.ShouldBe(0);
        invocation!.Arguments.Limit.ShouldBe(5);
    }

    [Theory]
    [InlineData("status", "--no-build")]
    [InlineData("bundle")]
    public async Task Standalone_runner_rejects_tool_only_options(params string[] args)
    {
        var (exitCode, invocation) = await Parse(false, args);

        exitCode.ShouldNotBe(0);
        invocation.ShouldBeNull();
    }

    [Fact]
    public async Task Standalone_runner_accepts_insecure_skip_host_key_check()
    {
        var (exitCode, invocation) = await Parse(false, "up", "--insecure-skip-host-key-check");

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.InsecureSkipHostKeyCheck.ShouldBeTrue();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Show_takes_an_id_with_history_and_no_statements(bool includeToolCommands)
    {
        var (exitCode, invocation) = await Parse(includeToolCommands, "show", "20260101", "--history", "5", "--no-statements");

        exitCode.ShouldBe(0);
        invocation.ShouldNotBeNull();
        invocation.Command.ShouldBe("show");
        invocation.Arguments.Id.ShouldBe("20260101");
        invocation.Arguments.Limit.ShouldBe(5);
        invocation.Arguments.NoStatements.ShouldBeTrue();
    }

    [Fact]
    public async Task Show_requires_an_id()
    {
        var (exitCode, invocation) = await Parse(true, "show");

        exitCode.ShouldNotBe(0);
        invocation.ShouldBeNull();
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    public async Task Up_and_down_can_turn_off_statement_capture(string command)
    {
        (await Parse(true, command)).Invocation!.Arguments.NoCaptureStatements.ShouldBeFalse();
        (await Parse(true, command, "--no-capture-statements")).Invocation!.Arguments.NoCaptureStatements.ShouldBeTrue();
    }

    [Theory]
    [InlineData("up")]
    [InlineData("plan")]
    public async Task Up_and_plan_take_the_collection_options(string command)
    {
        var none = (await Parse(true, command)).Invocation!.Arguments;
        (none.Only, none.AllowRebuild, none.AllowUnmatched, none.AllowSuperseded).ShouldBe((null, null, null, false));

        var (exitCode, invocation) = await Parse(true, command,
            "--only", "collections", "--allow-rebuild", "users.email_1", "--allow-rebuild", "orders.status_1", "--allow-unmatched", "users", "--allow-superseded");

        exitCode.ShouldBe(0);
        var arguments = invocation!.Arguments;
        arguments.Only.ShouldBe("collections");
        arguments.AllowRebuild.ShouldBe(["users.email_1", "orders.status_1"]);
        arguments.AllowUnmatched.ShouldBe(["users"]);
        arguments.AllowSuperseded.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_accepts_migrations_or_collections()
    {
        (await Parse(true, "up", "--only", "migrations")).Invocation!.Arguments.Only.ShouldBe("migrations");
        (await Parse(true, "up", "--only", "indexes")).ExitCode.ShouldNotBe(0);
    }
}

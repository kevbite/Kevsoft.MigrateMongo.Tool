using Kevsoft.MigrateMongo.Tool;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tool;

[Collection(nameof(ProcessEnvironmentCollection))]
public class ProcessRunnerTests
{
    [Fact]
    public void Defaults_dotnet_variables_when_not_set()
    {
        using var _ = new EnvironmentVariables(("DOTNET_CLI_TELEMETRY_OPTOUT", null), ("DOTNET_NOLOGO", null));

        var info = ProcessRunner.StartInfo("dotnet", ["build"], Directory.GetCurrentDirectory());

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"].ShouldBe("1");
        info.Environment["DOTNET_NOLOGO"].ShouldBe("1");
    }

    [Fact]
    public void Keeps_dotnet_variables_set_by_the_user()
    {
        using var _ = new EnvironmentVariables(("DOTNET_CLI_TELEMETRY_OPTOUT", "0"), ("DOTNET_NOLOGO", "false"));

        var info = ProcessRunner.StartInfo("dotnet", ["build"], Directory.GetCurrentDirectory());

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"].ShouldBe("0");
        info.Environment["DOTNET_NOLOGO"].ShouldBe("false");
    }

    private sealed class EnvironmentVariables : IDisposable
    {
        private readonly (string Name, string? Value)[] _original;

        public EnvironmentVariables(params (string Name, string? Value)[] variables)
        {
            _original = variables.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public class ProcessEnvironmentCollection;

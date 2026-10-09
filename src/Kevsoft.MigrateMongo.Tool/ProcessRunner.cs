using System.Diagnostics;
using System.Text;

namespace Kevsoft.MigrateMongo.Tool;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    /// <summary>The <c>dotnet</c> host: the one running us under <c>dotnet tool run</c>, otherwise from PATH.</summary>
    public static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    public static ProcessStartInfo StartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] ??= "1";
        info.Environment["DOTNET_NOLOGO"] ??= "1";
        return info;
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = Process.Start(StartInfo(fileName, arguments, workingDirectory))
                            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}

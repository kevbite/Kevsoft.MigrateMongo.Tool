using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Kevsoft.MigrateMongo.Protocol;

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

        info.Environment.TryAdd("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        info.Environment.TryAdd("DOTNET_NOLOGO", "1");
        return info;
    }

    /// <summary>
    /// Starts a process. One that can't be started is a <see cref="ToolException"/>: most often dotnet on a machine
    /// without the .NET SDK, such as in the <c>-bundle</c> Docker image.
    /// </summary>
    public static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new ToolException(ExitCodes.Failure, $"Failed to start '{info.FileName}'.");
        }
        catch (Win32Exception e)
        {
            var reason = new Win32Exception(e.NativeErrorCode).Message.TrimEnd('.');
            var dotnet = string.Equals(Path.GetFileNameWithoutExtension(info.FileName), "dotnet", StringComparison.OrdinalIgnoreCase);
            throw new ToolException(ExitCodes.Failure, dotnet
                ? $"Couldn't run dotnet ({reason}). Building the migrations project needs the .NET SDK; without it, pass --runner with a bundle made by `migrate-mongo bundle`."
                : $"Couldn't run '{info.FileName}' ({reason}).");
        }
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = Start(StartInfo(fileName, arguments, workingDirectory));
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

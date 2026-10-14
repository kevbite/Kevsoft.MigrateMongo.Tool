using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Tool;

Console.OutputEncoding = new UTF8Encoding(false);

using var cancellation = new CancellationTokenSource();
var cancelRequests = 0;

// First Ctrl+C/SIGTERM asks the runner to stop gracefully; the second one lets the process die.
bool CancelGracefully()
{
    if (Interlocked.Increment(ref cancelRequests) != 1)
    {
        return false;
    }

    cancellation.Cancel();
    return true;
}

Console.CancelKeyPress += (_, e) => e.Cancel = CancelGracefully();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = CancelGracefully());

var root = MigrateMongoCli.Create(
    $"migrate-mongo {ToolInfo.Version}: MongoDB migrations for .NET",
    includeToolCommands: true,
    (invocation, ct) => new ToolCommands(Environment.CurrentDirectory).RunAsync(invocation, ct));

// System.CommandLine's own handler would end the process 2s after Ctrl+C/SIGTERM, cutting the graceful stop short.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null }, cancellation.Token);

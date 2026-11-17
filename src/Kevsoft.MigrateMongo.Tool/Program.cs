using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text;
using Kevsoft.MigrateMongo;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Protocol;
using Kevsoft.MigrateMongo.Tool;

Console.OutputEncoding = new UTF8Encoding(false);

// For JavaScript-only projects the tool is its own runner: it spawns itself, speaking the runner protocol.
if (args.Length > 0 && args[0] is ProtocolVersions.HandshakeArgument or ProtocolVersions.ProtocolArgument)
{
    var runnerOptions = new MigrateMongoRunnerOptions();
    runnerOptions.Assemblies.Add(typeof(ToolCommands).Assembly);
    return await MigrateMongoRunner.RunAsync(args, runnerOptions);
}

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

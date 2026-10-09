using System.Text;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Tool;

Console.OutputEncoding = new UTF8Encoding(false);

using var cancellation = new CancellationTokenSource();
var cancelRequests = 0;
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C asks the runner to stop gracefully; the second one lets the process die.
    if (Interlocked.Increment(ref cancelRequests) == 1)
    {
        e.Cancel = true;
        cancellation.Cancel();
    }
};

var root = MigrateMongoCli.Create(
    $"migrate-mongo {ToolInfo.Version}: MongoDB migrations for .NET",
    includeToolCommands: true,
    (invocation, ct) => new ToolCommands(Environment.CurrentDirectory).RunAsync(invocation, ct));

return await root.Parse(args).InvokeAsync(cancellationToken: cancellation.Token);

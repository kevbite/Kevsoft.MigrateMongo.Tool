using System.Text.Json;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.Logging;

namespace Kevsoft.MigrateMongo.Runner;

/// <summary>Writes each event as a JSON line (the runner protocol, also used for <c>--json</c>).</summary>
internal sealed class JsonLineEventSink(TextWriter output) : IMigrationEventSink
{
    private readonly object _gate = new();

    public void OnEvent(RunnerEvent runnerEvent)
    {
        var json = JsonSerializer.Serialize(runnerEvent, ProtocolJsonContext.Default.RunnerEvent);
        lock (_gate)
        {
            output.WriteLine(json);
            output.Flush();
        }
    }

    public void Log(string level, string message) => OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = level, Message = message });

    public void Result(CommandResult result) => OnEvent(new RunnerEvent { Type = EventTypes.Result, Result = result });
}

internal sealed class RendererEventSink(ConsoleRenderer renderer) : IMigrationEventSink
{
    private readonly object _gate = new();

    public void OnEvent(RunnerEvent runnerEvent)
    {
        lock (_gate)
        {
            renderer.Render(runnerEvent);
        }
    }
}

/// <summary>Forwards migration logging to the event stream so the tool can show it.</summary>
internal sealed class EventSinkLoggerFactory(IMigrationEventSink sink) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new EventSinkLogger(sink);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class EventSinkLogger(IMigrationEventSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            sink.OnEvent(new RunnerEvent
            {
                Type = EventTypes.Log,
                Level = logLevel switch
                {
                    LogLevel.Trace => "trace",
                    LogLevel.Debug => "debug",
                    LogLevel.Information => "info",
                    LogLevel.Warning => "warning",
                    LogLevel.Error => "error",
                    _ => "critical",
                },
                Message = message,
            });
        }
    }
}

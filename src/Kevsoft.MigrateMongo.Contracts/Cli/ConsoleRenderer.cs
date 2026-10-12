using System.Globalization;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Cli;

/// <summary>Renders runner events and results as human readable text.</summary>
public sealed class ConsoleRenderer(TextWriter output, bool color)
{
    private const string Reset = "\u001b[0m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Cyan = "\u001b[36m";
    private const string Dim = "\u001b[2m";

    public void Render(RunnerEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Log:
                var colour = e.Level switch { "error" or "critical" => Red, "warning" => Yellow, "debug" or "trace" => Dim, _ => null };
                WriteLine($"  {Paint(e.Level ?? "info", colour)}: {e.Message}");
                break;
            case EventTypes.LockWaiting:
                WriteLine(Paint($"… {e.Message}", Yellow));
                break;
            case EventTypes.LockAcquired:
                WriteLine(Paint(e.Message ?? "", Dim));
                break;
            case EventTypes.MigrationStarted:
                WriteLine(Paint($"{(e.Direction == "down" ? "◀" : "▶")} {e.MigrationId} {e.Name}", Cyan));
                break;
            case EventTypes.MigrationCompleted:
                var details = FormatDuration(e.DurationMs) + (e.Transactional == true ? ", transaction" : "");
                WriteLine(Paint($"✔ {e.MigrationId} {e.Name}", Green) + $"   ({details})");
                break;
            case EventTypes.MigrationFailed:
                WriteLine(Paint($"✖ {e.MigrationId} {e.Name}: {e.Error}", Red));
                break;
            case EventTypes.Result when e.Result is not null:
                RenderResult(e.Result);
                break;
        }
    }

    public void RenderResult(CommandResult result)
    {
        if (result.Migrations is { } migrations)
        {
            if (migrations.Count == 0)
            {
                WriteLine("No migrations found.");
            }
            else
            {
                WriteTable(
                    ["ID", "Name", "State", "Batch", "Applied"],
                    migrations.Select(m => new[]
                    {
                        m.Id,
                        m.Name + (m.ChecksumMismatch ? " (modified)" : "") + (m.InCode ? "" : " (missing)"),
                        m.State,
                        m.Batch?.ToString(CultureInfo.InvariantCulture) ?? "",
                        m.AppliedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    }).ToList(),
                    row => row[2] switch { "applied" => Green, "pending" => Yellow, "failed" or "applying" or "rollingBack" => Red, _ => null });
            }
        }

        if (result.Plan is { Count: > 0 } plan)
        {
            foreach (var item in plan)
            {
                WriteLine($"  {item.Id}  {item.Name}   ({(item.Transactional ? "transaction" : "no transaction")})");
            }
        }

        if (result.History is { } history)
        {
            if (history.Count == 0)
            {
                WriteLine("No history.");
            }
            else
            {
                WriteTable(
                    ["At", "ID", "Name", "Event", "Batch", "Run", "Host", "Error"],
                    history.Select(h => new[]
                    {
                        h.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        h.MigrationId,
                        h.Name,
                        h.Event,
                        h.Batch?.ToString(CultureInfo.InvariantCulture) ?? "",
                        h.RunId.Length > 8 ? h.RunId[..8] : h.RunId,
                        h.Host ?? "",
                        h.Error ?? "",
                    }).ToList(),
                    row => row[3] == "failed" ? Red : null);
            }
        }

        if (result.Issues is { Count: > 0 } issues && result.ExitCode is ExitCodes.Success or ExitCodes.PendingMigrations or ExitCodes.ValidationFailed)
        {
            foreach (var issue in issues)
            {
                WriteLine(Paint($"{issue.Severity}: {issue.Message}", issue.Severity == "error" ? Red : Yellow));
            }
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            var failed = result.ExitCode is not (ExitCodes.Success or ExitCodes.PendingMigrations);
            WriteLine(failed ? Paint($"✖ {result.Message}", Red) : result.Message);
        }
    }

    public void Error(string message) => WriteLine(Paint($"✖ {message}", Red));

    public void Info(string message) => WriteLine(message);

    public void Muted(string message) => WriteLine(Paint(message, Dim));

    public void Warning(string message) => WriteLine(Paint($"⚠ {message}", Yellow));

    private void WriteTable(string[] headers, List<string[]> rows, Func<string[], string?> rowColour)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        WriteLine(" " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in rows)
        {
            WriteLine(Paint(" " + string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd(), rowColour(row)));
        }
    }

    private static string FormatDuration(long? durationMs) =>
        ((durationMs ?? 0) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";

    private string Paint(string text, string? colour) => color && colour is not null ? colour + text + Reset : text;

    private void WriteLine(string text)
    {
        output.WriteLine(text);
        output.Flush();
    }
}

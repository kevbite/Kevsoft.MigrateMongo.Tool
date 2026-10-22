using System.Globalization;
using System.Text.Json;
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
                RenderOutputSummary(e.Output, e.MigrationId, failed: false);
                break;
            case EventTypes.MigrationFailed:
                WriteLine(Paint($"✖ {e.MigrationId} {e.Name}: {e.Error}", Red));
                RenderOutputSummary(e.Output, e.MigrationId, failed: true);
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
                var withResults = migrations.Any(m => HasResults(m.Output));
                WriteTable(
                    withResults ? ["ID", "Name", "State", "Batch", "Applied", "Results"] : ["ID", "Name", "State", "Batch", "Applied"],
                    migrations.Select(m => new[]
                    {
                        m.Id,
                        m.Name + (m.ChecksumMismatch ? " (modified)" : "") + (m.InCode ? "" : " (missing)"),
                        m.State,
                        m.Batch?.ToString(CultureInfo.InvariantCulture) ?? "",
                        m.AppliedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                        withResults ? ResultsColumn(m.Output) : null,
                    }.OfType<string>().ToArray()).ToList(),
                    row => row[2] switch { "applied" => Green, "pending" => Yellow, "failed" or "applying" or "rollingBack" => Red, _ => null });
            }
        }

        if (result.Show is { } show)
        {
            RenderShow(show);
        }

        if (result.Plan is { Count: > 0 } plan)
        {
            foreach (var item in plan)
            {
                WriteLine($"  {(item.Direction == "down" ? "◀" : "▶")} {item.Id}  {item.Name}   ({(item.Transactional ? "transaction" : "no transaction")})");
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
                WriteHistoryTable(history);
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

    private void WriteHistoryTable(List<HistoryRecord> history)
    {
        var withResults = history.Any(h => HasResults(h.Output));
        WriteTable(
            withResults
                ? ["At", "ID", "Name", "Event", "Batch", "Run", "Host", "Error", "Results"]
                : ["At", "ID", "Name", "Event", "Batch", "Run", "Host", "Error"],
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
                withResults ? ResultsColumn(h.Output) : null,
            }.OfType<string>().ToArray()).ToList(),
            row => row[3] == "failed" ? Red : null);
    }

    /// <summary>The indented lines under a ✔/✖ line: each result, and a pointer to <c>show</c> for the operations.</summary>
    private void RenderOutputSummary(MigrationOutputInfo? output, string? id, bool failed)
    {
        if (output is null)
        {
            return;
        }

        if (output.Results is { ValueKind: JsonValueKind.Object } results)
        {
            var properties = results.EnumerateObject().ToList();
            var width = properties.Count == 0 ? 0 : properties.Max(p => p.Name.Length);
            foreach (var property in properties)
            {
                WriteLine($"    {property.Name.PadRight(width)}  {Summarise(property.Value)}");
            }
        }

        var operations = (output.Operations?.Count ?? 0) + (output.OperationsTruncated ?? 0);
        if (operations > 0)
        {
            WriteLine(Paint($"    {operations} operation(s) recorded – migrate-mongo show {id}", Dim));
        }

        if (failed && !output.Committed)
        {
            WriteLine(Paint("    (transaction aborted – changes not persisted)", Yellow));
        }
    }

    private void RenderShow(ShowResult show)
    {
        var m = show.Migration;
        WriteLine(Paint($"{m.Id} {m.Name}", Cyan));
        var rows = new List<(string Label, string Value)>
        {
            ("State", m.State + Parenthesise(Join(
                m.Batch is { } batch ? $"batch {batch.ToString(CultureInfo.InvariantCulture)}" : null,
                show.Direction,
                m.Transactional switch { true => "transaction", false => "no transaction", _ => null }))),
        };
        if (m.AppliedAt is { } appliedAt)
        {
            var by = (show.User, m.Host) switch
            {
                ({ Length: > 0 } user, { Length: > 0 } host) => $" by {user}@{host}",
                (_, { Length: > 0 } host) => $" on {host}",
                _ => "",
            };
            rows.Add(("Applied", appliedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + by +
                                 (show.DurationMs is { } duration ? $" ({FormatDuration(duration)})" : "")));
        }

        if (m.Checksum is { } checksum)
        {
            rows.Add(("Checksum", checksum + (show.CodeChecksum is { } code && code != checksum && m.State != "pending" ? $" (modified: the code is {code})" : "")));
        }

        if (!m.InCode)
        {
            rows.Add(("Code", "missing (no longer in the code)"));
        }

        if (show.Origin is { Length: > 0 } origin)
        {
            rows.Add(("Source", $"{show.Source} {origin}".Trim()));
        }

        var versions = Join(
            show.Tool is { Length: > 0 } tool ? $"tool {tool}" : null,
            show.Library is { Length: > 0 } library ? $"library {library}" : null,
            show.Driver is { Length: > 0 } driver ? $"driver {driver}" : null);
        if (versions.Length > 0)
        {
            rows.Add(("Versions", versions));
        }

        if (m.RunId is { Length: > 0 } runId)
        {
            rows.Add(("Run", runId));
        }

        if (m.Error is { } error)
        {
            rows.Add(("Error", error));
        }

        foreach (var (label, value) in rows)
        {
            WriteLine($"  {label,-9} {value}");
        }

        var output = m.Output;
        if (output?.Results is { ValueKind: JsonValueKind.Object } results && results.EnumerateObject().Any())
        {
            WriteLine("");
            WriteLine("Results" + (output.Committed ? "" : Paint("  (transaction aborted – changes not persisted)", Yellow)));
            var properties = results.EnumerateObject().ToList();
            var width = properties.Max(p => p.Name.Length);
            foreach (var property in properties)
            {
                WriteLine($"  {property.Name.PadRight(width)}  {ExtendedJson.Format(property.Value, quoteStrings: true)}");
            }
        }

        if (output?.Notes is { Count: > 0 } notes)
        {
            WriteLine("");
            WriteLine("Notes");
            foreach (var note in notes)
            {
                WriteLine(Paint($"  {note.At:HH:mm:ss}  {note.Level,-7}  {note.Message}", note.Level == "warning" ? Yellow : null));
            }
        }

        if (output?.NotesTruncated is > 0)
        {
            WriteLine(Paint($"  (+{output.NotesTruncated} more not recorded)", Dim));
        }

        if (output?.Operations is { Count: > 0 } operations)
        {
            WriteLine("");
            WriteLine("Operations");
            for (var i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                var counts = op.Counts is { Count: > 0 } c ? "  " + string.Join(" · ", c.Select(p => $"{p.Key} {p.Value.ToString(CultureInfo.InvariantCulture)}")) : "";
                WriteLine($"  {i + 1}. {op.Kind}{(op.Collection is null ? "" : " " + op.Collection)}{(op.Label is null ? "" : $" [{op.Label}]")}{counts}" +
                          Paint($"  ({FormatDuration(op.DurationMs)})", Dim));
                if (op.Statement is { ValueKind: JsonValueKind.Object } statement)
                {
                    foreach (var part in statement.EnumerateObject())
                    {
                        WriteLine(Paint($"       {part.Name}: {ExtendedJson.Format(part.Value, quoteStrings: true)}", Dim));
                    }
                }

                if (op.StatementTruncated == true)
                {
                    WriteLine(Paint("       (statement too large to record)", Dim));
                }
            }
        }

        if (output?.OperationsTruncated is > 0)
        {
            WriteLine(Paint($"  (+{output.OperationsTruncated} more not recorded)", Dim));
        }

        if (show.History.Count > 0)
        {
            WriteLine("");
            WriteLine("History");
            WriteHistoryTable(show.History);
        }
    }

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Parenthesise(string text) => text.Length == 0 ? "" : $" ({text})";

    private static bool HasResults(MigrationOutputInfo? output) =>
        output?.Results is { ValueKind: JsonValueKind.Object } results && results.EnumerateObject().Any();

    /// <summary>The results on one line for a table, e.g. <c>skipped: 3, archiveOld: matched 1520 · modified 1518</c>.</summary>
    private static string ResultsColumn(MigrationOutputInfo? output)
    {
        if (!HasResults(output))
        {
            return "";
        }

        const int MaxWidth = 60;
        var text = string.Join(", ", output!.Results!.Value.EnumerateObject().Select(p => $"{p.Name}: {Summarise(p.Value)}"));
        return text.Length > MaxWidth ? text[..(MaxWidth - 1)] + "…" : text;
    }

    /// <summary>
    /// A short form of a result value: counts as <c>matched 1520 · modified 1518</c>, arrays as <c>[n items]</c>, and
    /// documents inline when they fit in 80 characters.
    /// </summary>
    private static string Summarise(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            return $"[{count} item{(count == 1 ? "" : "s")}]";
        }

        if (ExtendedJson.IsContainer(value))
        {
            var properties = value.EnumerateObject().ToList();
            if (properties.Count > 0 && properties.All(p => ExtendedJson.TryGetInteger(p.Value, out _)))
            {
                return string.Join(" · ", properties.Select(p =>
                {
                    ExtendedJson.TryGetInteger(p.Value, out var n);
                    return $"{p.Name} {n.ToString(CultureInfo.InvariantCulture)}";
                }));
            }

            var text = ExtendedJson.Format(value, quoteStrings: true);
            return text.Length > 80 ? "{…}" : text;
        }

        return ExtendedJson.Format(value);
    }

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

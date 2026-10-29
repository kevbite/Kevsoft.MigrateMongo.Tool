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
            case EventTypes.CollectionStarted:
                WriteLine(Paint($"⟳ {e.Collection}", Cyan));
                break;
            case EventTypes.CollectionCompleted when e.Changes is { Count: > 0 } changes:
                WriteChanges(changes, "  ");
                WriteLine(Paint($"✔ {e.Collection}", Green) + $"   ({FormatDuration(e.DurationMs)})");
                break;
            case EventTypes.CollectionCompleted:
                WriteLine(Paint($"✔ {e.Collection}   (in sync)", Dim));
                break;
            case EventTypes.CollectionFailed:
                WriteLine(Paint($"✖ {e.Collection}: {e.Error}", Red));
                RenderOutputSummary(e.Output, e.Collection, failed: false);
                break;
            case EventTypes.CollectionSkipped:
                WriteLine(Paint($"⚠ {e.Message}", Yellow));
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

        if (result.Migrations is not null && result.Collections is { Count: > 0 } statuses)
        {
            WriteLine("");
            WriteTable(
                ["Collection", "State", "Reconciled", "Changes"],
                statuses.Select(c => new[]
                {
                    c.Name,
                    StateText(c.State),
                    c.ReconciledAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                    ChangeCounts(c.Changes),
                }).ToList(),
                row => row[1] switch { "in sync" => Green, "changes" or "superseded" => Yellow, "blocked" => Red, _ => null });
        }

        if (result.Show is { } show)
        {
            RenderShow(show);
        }

        if (result.ShowCollection is { } collection)
        {
            RenderShowCollection(collection);
        }

        if (result.Plan is { Count: > 0 } plan)
        {
            foreach (var item in plan)
            {
                WriteLine($"  {(item.Direction == "down" ? "◀" : "▶")} {item.Id}  {item.Name}   ({(item.Transactional ? "transaction" : "no transaction")})");
            }
        }

        if (result.Migrations is null && result.Collections is { } collections)
        {
            foreach (var item in collections)
            {
                if (item.Changes.Count == 0)
                {
                    WriteLine(Paint($"  ⟳ {item.Name}   {StateText(item.State)}", item.State == CollectionStates.InSync ? Dim : Yellow));
                    continue;
                }

                WriteLine($"  ⟳ {item.Name}" + (item.State == CollectionStates.Superseded ? Paint("   superseded (skipped)", Yellow) : ""));
                WriteChanges(item.Changes, "      ");
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

        RenderOutputDetail(m.Output);
        if (show.History.Count > 0)
        {
            WriteLine("");
            WriteLine("History");
            WriteHistoryTable(show.History);
        }
    }

    private void RenderShowCollection(CollectionInfo collection)
    {
        WriteLine(Paint($"{collection.Name} (collection)", Cyan));
        var rows = new List<(string Label, string Value)> { ("State", StateText(collection.State)) };
        if (collection.ReconciledAt is { } reconciledAt)
        {
            var by = (collection.User, collection.Host) switch
            {
                ({ Length: > 0 } user, { Length: > 0 } host) => $" by {user}@{host}",
                (_, { Length: > 0 } host) => $" on {host}",
                _ => "",
            };
            rows.Add(("Reconciled", reconciledAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + by +
                                    (collection.DurationMs is { } duration ? $" ({FormatDuration(duration)})" : "")));
        }

        if (collection.Declaration is { } declaration)
        {
            rows.Add(("Declared", declaration));
        }

        if (collection.Origin is { Length: > 0 } origin)
        {
            rows.Add(("Source", $"{collection.Source} {origin}".Trim()));
        }

        if (collection.RunId is { Length: > 0 } runId)
        {
            rows.Add(("Run", runId));
        }

        foreach (var (label, value) in rows)
        {
            WriteLine($"  {label,-10} {value}");
        }

        if (collection.Indexes is { Count: > 0 } indexes)
        {
            WriteLine("");
            WriteLine("Indexes");
            var width = indexes.Max(i => i.Name.Length);
            foreach (var index in indexes)
            {
                var options = index.Options is { ValueKind: JsonValueKind.Object } o ? "  " + FormatOptions(o) : "";
                var status = index.Status switch
                {
                    "undeclared" => "  undeclared" + (index.HiddenAt is { } hiddenAt ? $", hidden {hiddenAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}" : ""),
                    "ignored" => "  ignored",
                    _ => index.DeclaredName is { } declaredName ? $"  (declared as {declaredName})" : "",
                };
                WriteLine($"  {index.Name.PadRight(width)}  {Keys(index.Keys)}{options}" +
                          Paint(status, index.Status == "undeclared" ? Yellow : Dim));
            }
        }

        if (collection.Changes.Count > 0)
        {
            WriteLine("");
            WriteLine("Pending");
            WriteChanges(collection.Changes, "  ");
        }

        foreach (var problem in collection.Problems ?? [])
        {
            WriteLine(Paint($"  ✖ {problem}", Red));
        }

        if (collection.Output is not null)
        {
            WriteLine("");
            WriteLine(Paint("Last change", Dim));
        }

        RenderOutputDetail(collection.Output);
        if (collection.History is { Count: > 0 } history)
        {
            WriteLine("");
            WriteLine("History");
            WriteHistoryTable(history);
        }
    }

    /// <summary>One line per change, e.g. <c>+ create   status_1   { status: 1 }</c>.</summary>
    private void WriteChanges(List<IndexChangeInfo> changes, string indent)
    {
        var width = changes.Max(c => c.Index.Length);
        foreach (var change in changes)
        {
            var (symbol, colour) = change.Action switch
            {
                IndexActions.Create => ("+", Green),
                IndexActions.Modify => ("~", Yellow),
                IndexActions.Rebuild => ("↻", Yellow),
                IndexActions.Hide or IndexActions.Drop => ("-", Red),
                _ => ("!", Dim),
            };
            var detail = change.Action == IndexActions.Create
                ? Keys(change.Keys) + (change.Detail is { } options ? "  " + options : "")
                : change.Detail ?? "";
            var line = $"{indent}{symbol} {change.Action,-8} {change.Index.PadRight(width)}   {detail}";
            WriteLine(change.Blocked == true
                ? Paint(line + $"   ✖ blocked: {change.Reason}", Red)
                : Paint(line, colour));
        }
    }

    private static string Keys(JsonElement? keys) => keys is { } k ? ExtendedJson.Format(k, quoteStrings: true) : "";

    /// <summary>e.g. <c>unique, expireAfterSeconds 3600</c>.</summary>
    private static string FormatOptions(JsonElement options) =>
        string.Join(", ", options.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.True
            ? p.Name
            : $"{p.Name} {ExtendedJson.Format(p.Value, quoteStrings: true)}"));

    private static string StateText(string state) => state switch
    {
        CollectionStates.InSync => "in sync",
        CollectionStates.Changes => "changes",
        CollectionStates.Blocked => "blocked",
        CollectionStates.Superseded => "superseded",
        _ => state,
    };

    /// <summary>e.g. <c>1 create · 2 hide · 1 kept</c>.</summary>
    private static string ChangeCounts(List<IndexChangeInfo> changes) =>
        string.Join(" · ", changes
            .GroupBy(c => c.Action)
            .Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} {(g.Key == IndexActions.Keep ? "kept" : g.Key)}"));

    /// <summary>The results, notes and operations a migration or collection change recorded.</summary>
    private void RenderOutputDetail(MigrationOutputInfo? output)
    {
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

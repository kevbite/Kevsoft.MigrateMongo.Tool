using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Kevsoft.MigrateMongo.Scripting;

/// <summary>
/// Migrations and collection definitions written in JavaScript: <c>migrations/*.js</c> and <c>collections/*.js</c> under
/// a root folder. They're discovered, ordered and run alongside C# migrations, with the same history, tracking and checks.
/// </summary>
public sealed class ScriptMigrationSource : IMigrationSource
{
    /// <summary>The <see cref="MigrationDescriptor.Source"/> of script migrations and definitions.</summary>
    public const string SourceName = "js";

    /// <summary>The folder (under the root) migrations are read from.</summary>
    public const string MigrationsFolder = "migrations";

    /// <summary>The folder (under the root) collection and view definitions are read from.</summary>
    public const string CollectionsFolder = "collections";

    private readonly string _root;
    private IReadOnlyCollection<MigrationDescriptor>? _migrations;
    private IReadOnlyCollection<CollectionDescriptor>? _collections;

    /// <param name="root">The folder holding <c>migrations/</c> and <c>collections/</c>. Scripts can only import files under it.</param>
    public ScriptMigrationSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>The root folder, as a full path.</summary>
    public string Root => _root;

    /// <inheritdoc/>
    /// <exception cref="ScriptException">A script can't be loaded or doesn't export a migration.</exception>
    public IReadOnlyCollection<MigrationDescriptor> GetMigrations() => _migrations ??= LoadMigrations();

    /// <inheritdoc/>
    /// <exception cref="ScriptException">A script can't be loaded or doesn't export definitions.</exception>
    public IReadOnlyCollection<CollectionDescriptor> GetCollections() => _collections ??= LoadCollections();

    private List<MigrationDescriptor> LoadMigrations()
    {
        var files = Files(MigrationsFolder);
        if (files.Count == 0)
        {
            return [];
        }

        using var host = new ScriptHost(_root, null, CancellationToken.None);
        var migrations = new List<MigrationDescriptor>();
        foreach (var file in files)
        {
            var (id, name) = ParseFileName(file);
            var source = File.ReadAllText(Path.Combine(_root, file));
            var exports = ScriptMigration.Exports(host, file);
            var root = _root;
            migrations.Add(new MigrationDescriptor(
                id,
                name,
                SourceName,
                file,
                ScriptSource.Checksum(source, file),
                exports.Transaction,
                _ => new ScriptMigration(root, file, id, name)));
        }

        return migrations;
    }

    private List<CollectionDescriptor> LoadCollections()
    {
        var files = Files(CollectionsFolder);
        if (files.Count == 0)
        {
            return [];
        }

        using var host = new ScriptHost(_root, null, CancellationToken.None);
        var isDefinition = host.Runtime.Get("isDefinition");
        var collections = new List<CollectionDescriptor>();
        foreach (var file in files)
        {
            var module = host.Import(file);
            var definitions = new List<JsValue>();
            foreach (var key in module.GetOwnPropertyKeys())
            {
                var value = module.Get(key);
                if (value.IsArray())
                {
                    var array = value.AsArray();
                    for (uint i = 0; i < array.Length; i++)
                    {
                        definitions.Add(array.Get(i));
                    }
                }
                else
                {
                    definitions.Add(value);
                }
            }

            definitions = definitions.Where(d => host.Call(isDefinition, d).AsBoolean()).ToList();
            if (definitions.Count == 0)
            {
                throw new ScriptException(
                    $"{file} doesn't export a collection or view definition. Export one, e.g. " +
                    "export default collection('orders', c => c.indexes.add({ customerId: 1 })), or an array of them.");
            }

            foreach (var definition in definitions)
            {
                collections.Add(ScriptDefinitions.ToDescriptor(host.ToBson(definition).AsBsonDocument, file));
            }
        }

        return collections;
    }

    /// <summary>The <c>.js</c> files directly in a folder, relative to the root with forward slashes, in ordinal order.</summary>
    private List<string> Files(string folder)
    {
        var directory = Path.Combine(_root, folder);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.js", SearchOption.TopDirectoryOnly)
            .Select(f => $"{folder}/{Path.GetFileName(f)}")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary><c>migrations/20261014164028-add-email-index.js</c> is id <c>20261014164028</c>, name <c>add-email-index</c>.</summary>
    internal static (string Id, string Name) ParseFileName(string file)
    {
        var fileName = Path.GetFileNameWithoutExtension(file);
        var separator = fileName.IndexOfAny(['-', '_']);
        if (separator <= 0 || separator == fileName.Length - 1 || !fileName[..separator].All(char.IsAsciiDigit))
        {
            throw new ScriptException(
                $"{file}: a migration's file name must be its id (digits, e.g. a timestamp) then its name, " +
                "like 20261014164028-add-email-index.js. Use 'migrate-mongo create <name>' to make one, and keep helpers in another folder.");
        }

        return (fileName[..separator], fileName[(separator + 1)..]);
    }
}

/// <summary>One JavaScript migration. Each run loads it into a new engine, so a retried transaction starts afresh.</summary>
internal sealed class ScriptMigration(string root, string file, string id, string name) : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => RunAsync("up", context, cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => RunAsync("down", context, cancellationToken);

    private Task RunAsync(string direction, MigrationContext context, CancellationToken cancellationToken) =>
        // Jint runs on one thread, and the script waits on database calls synchronously, so give it a thread of its own.
        Task.Factory.StartNew(() =>
        {
            using var host = new ScriptHost(root, context, cancellationToken);
            var exports = Exports(host, file);
            var function = direction == "up" ? exports.Up : exports.Down
                ?? throw new ScriptException($"{file} has no down function, so it can't be rolled back. Export one: export async function down(db, context) {{ ... }}");
            host.CallAndWait(file, function, host.CreateDb(), host.CreateContext(id, name));
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    internal sealed record MigrationExports(JsValue Up, JsValue? Down, TransactionMode Transaction);

    /// <summary>
    /// A migration exports <c>up</c> (and optionally <c>down</c> and <c>transaction</c>) by name, or as a default export
    /// object (<c>export default defineMigration({ up, down })</c>, or CommonJS <c>module.exports = { up, down }</c>).
    /// </summary>
    internal static MigrationExports Exports(ScriptHost host, string file)
    {
        ObjectInstance module = host.Import(file);
        if (module.Get("default") is ObjectInstance defaultExport && defaultExport.Get("up").IsCallable())
        {
            module = defaultExport;
        }

        var up = module.Get("up");
        if (!up.IsCallable())
        {
            throw new ScriptException($"{file} doesn't export an up function. Export one: export async function up(db, context) {{ ... }}");
        }

        var down = module.Get("down");
        if (!down.IsUndefined() && !down.IsCallable())
        {
            throw new ScriptException($"{file}: down must be a function.");
        }

        var transaction = module.Get("transaction");
        var mode = transaction.IsUndefined()
            ? TransactionMode.Auto
            : transaction.IsString() ? transaction.AsString() switch
            {
                "auto" => TransactionMode.Auto,
                "always" => TransactionMode.Always,
                "never" => TransactionMode.Never,
                _ => (TransactionMode?)null,
            } : null;

        return new MigrationExports(up, down.IsUndefined() ? null : down,
            mode ?? throw new ScriptException($"{file}: transaction must be 'auto', 'always' or 'never', not {transaction}."));
    }
}

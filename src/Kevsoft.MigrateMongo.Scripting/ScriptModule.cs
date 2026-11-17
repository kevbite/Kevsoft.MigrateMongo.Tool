using System.Collections.Concurrent;

namespace Kevsoft.MigrateMongo.Scripting;

/// <summary>
/// The <c>@kevsoft/migrate-mongo</c> module that scripts import, and its TypeScript declarations for editors and
/// type checking (<c>migrate-mongo init --js</c> writes them to <c>types/migrate-mongo.d.ts</c>).
/// </summary>
public static class ScriptModule
{
    /// <summary>The specifier scripts import the API from.</summary>
    public const string Name = "@kevsoft/migrate-mongo";

    private static readonly ConcurrentDictionary<string, string> Resources = new();

    /// <summary>The TypeScript declarations (<c>.d.ts</c>) for <see cref="Name"/>.</summary>
    public static string Declarations => Read("migrate-mongo.d.ts");

    internal static string Runtime => Read("migrate-mongo.js");

    private static string Read(string name) => Resources.GetOrAdd(name, static n =>
    {
        using var stream = typeof(ScriptModule).Assembly.GetManifestResourceStream(n)
                           ?? throw new InvalidOperationException($"Embedded resource {n} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}

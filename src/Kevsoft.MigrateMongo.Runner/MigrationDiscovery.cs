using System.Reflection;

namespace Kevsoft.MigrateMongo.Runner;

internal static class MigrationDiscovery
{
    private const string AbstractionsAssembly = "Kevsoft.MigrateMongo.Abstractions";

    // .NET, the MongoDB driver and this library, which never contain migrations.
    private static readonly string[] SkippedAssemblies = ["System", "Microsoft", "MongoDB.Bson", "MongoDB.Driver", "Kevsoft.MigrateMongo"];

    public static (List<MigrationDescriptor> Migrations, List<CollectionDescriptor> Collections) Discover(MigrateMongoRunnerOptions options, Assembly? entryAssembly)
    {
        var assemblies = options.Assemblies.Count > 0
            ? options.Assemblies.Distinct().ToList()
            : CandidateAssemblies(entryAssembly);

        var sources = assemblies
            .Select(MigrationRegistry.ForAssembly)
            .OfType<IMigrationSource>()
            .Concat(options.Sources)
            .Distinct()
            .ToList();

        return (sources.SelectMany(s => s.GetMigrations()).ToList(), sources.SelectMany(s => s.GetCollections()).ToList());
    }

    /// <summary>The entry assembly plus any directly referenced assembly that references the abstractions.</summary>
    private static List<Assembly> CandidateAssemblies(Assembly? entryAssembly)
    {
        var result = new List<Assembly>();
        if (entryAssembly is null)
        {
            return result;
        }

        result.Add(entryAssembly);
        foreach (var reference in entryAssembly.GetReferencedAssemblies())
        {
            if (reference.Name is null || IsSkipped(reference.Name))
            {
                continue;
            }

            try
            {
                var assembly = Assembly.Load(reference);
                if (assembly.GetReferencedAssemblies().Any(r => r.Name == AbstractionsAssembly))
                {
                    result.Add(assembly);
                }
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // Not loadable; it can't contain migrations we can run.
            }
        }

        return result;
    }

    /// <summary>
    /// Whether an assembly is one of <see cref="SkippedAssemblies"/> or below it, matching whole dotted segments so
    /// that projects such as <c>MongoDBShop.Migrations</c> and <c>MongoDB.Migrations</c> are still scanned.
    /// </summary>
    internal static bool IsSkipped(string name) => SkippedAssemblies.Any(skipped =>
        name.StartsWith(skipped, StringComparison.Ordinal) && (name.Length == skipped.Length || name[skipped.Length] == '.'));
}

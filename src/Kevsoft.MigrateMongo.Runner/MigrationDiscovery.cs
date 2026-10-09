using System.Reflection;

namespace Kevsoft.MigrateMongo.Runner;

internal static class MigrationDiscovery
{
    private const string AbstractionsAssembly = "Kevsoft.MigrateMongo.Abstractions";

    public static List<MigrationDescriptor> Discover(MigrateMongoRunnerOptions options, Assembly? entryAssembly)
    {
        var assemblies = options.Assemblies.Count > 0
            ? options.Assemblies.Distinct().ToList()
            : CandidateAssemblies(entryAssembly);

        var sources = assemblies
            .Select(MigrationRegistry.ForAssembly)
            .OfType<IMigrationSource>()
            .Concat(options.Sources)
            .Distinct();

        return sources.SelectMany(s => s.GetMigrations()).ToList();
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
            if (reference.Name is null || reference.Name.StartsWith("System", StringComparison.Ordinal) ||
                reference.Name.StartsWith("Microsoft", StringComparison.Ordinal) ||
                reference.Name.StartsWith("MongoDB", StringComparison.Ordinal) ||
                reference.Name.StartsWith("Kevsoft.MigrateMongo", StringComparison.Ordinal))
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
}

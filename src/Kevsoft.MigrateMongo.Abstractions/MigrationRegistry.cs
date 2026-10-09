using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Kevsoft.MigrateMongo;

/// <summary>
/// Registry populated by the source generator (via a module initializer in each assembly containing migrations).
/// </summary>
public static class MigrationRegistry
{
    private static readonly ConcurrentDictionary<Assembly, IMigrationSource> Sources = new();

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Register(Assembly assembly, IMigrationSource source) => Sources[assembly] = source;

    /// <summary>
    /// Gets the generated migration source for an assembly, forcing its module initializer to run first.
    /// Returns <c>null</c> if the assembly contains no migrations.
    /// </summary>
    public static IMigrationSource? ForAssembly(Assembly assembly)
    {
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        return Sources.TryGetValue(assembly, out var source) ? source : null;
    }

    /// <summary>All sources registered so far.</summary>
    public static IReadOnlyCollection<IMigrationSource> All => Sources.Values.ToArray();
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kevsoft.MigrateMongo.Tool;

internal static class ToolInfo
{
    /// <summary>MongoDB.Driver version written into newly scaffolded projects.</summary>
    public const string DefaultDriverVersion = "3.13.0";

    public static string Version { get; } =
        typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string Mode => RuntimeFeature.IsDynamicCodeSupported ? $".NET {Environment.Version}" : "aot";

    public static string Description => $"migrate-mongo/{Version} ({RuntimeInformation.RuntimeIdentifier}; {Mode})";
}

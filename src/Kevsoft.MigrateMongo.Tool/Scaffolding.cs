using System.Globalization;
using System.Text;

namespace Kevsoft.MigrateMongo.Tool;

internal static class Scaffolding
{
    public static string ConfigFile(string projectRelativePath, string databaseName) => $"""
        # Kevsoft.MigrateMongo configuration. See https://github.com/kevbite/Kevsoft.MigrateMongo.Tool
        project: {projectRelativePath}
        mongodb:
          url: ${"{"}MONGODB_URL{"}"}
          databaseName: {databaseName}
        stateCollection: _migrations
        lockTtl: 300
        lockWaitTimeout: 60
        onChecksumMismatch: fail   # fail | warn | ignore
        transaction: auto          # auto | always | never
        # environments:
        #   prod:
        #     mongodb:
        #       url: ${"{"}PROD_MONGODB_URL{"}"}

        """;

    public static string ProjectFile(string targetFramework, string runnerVersion, string driverVersion) => $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>{targetFramework}</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>

          <ItemGroup>
            <PackageReference Include="Kevsoft.MigrateMongo.Runner" Version="{runnerVersion}" />
            <PackageReference Include="MongoDB.Driver" Version="{driverVersion}" />
          </ItemGroup>

        </Project>

        """;

    public const string ProgramFile = """
        using Kevsoft.MigrateMongo;

        return await MigrateMongoRunner.RunAsync(args);

        """;

    public static string MigrationFile(string @namespace, string id, string className, string name) => $$"""
        using Kevsoft.MigrateMongo;
        using MongoDB.Bson;
        using MongoDB.Driver;

        namespace {{@namespace}};

        [Migration("{{id}}", "{{name}}")]
        public sealed class {{className}} : IMigration
        {
            public Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public Task DownAsync(MigrationContext context, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }

        """;

    /// <summary>Renders a user's <c>migrationTemplate</c>. Anything other than the known placeholders is left untouched.</summary>
    public static string RenderMigrationTemplate(string template, string @namespace, string id, string className, string name) =>
        template
            .Replace("{{namespace}}", @namespace, StringComparison.Ordinal)
            .Replace("{{id}}", id, StringComparison.Ordinal)
            .Replace("{{className}}", className, StringComparison.Ordinal)
            .Replace("{{name}}", name, StringComparison.Ordinal);

    /// <summary>Turns user input such as <c>add email-index</c> into a C# class name (<c>AddEmailIndex</c>).</summary>
    public static string? ToClassName(string input)
    {
        var builder = new StringBuilder();
        var upperNext = true;
        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                builder.Append(upperNext ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        return builder.Length > 0 && char.IsLetter(builder[0]) ? builder.ToString() : null;
    }

    /// <summary><c>AddEmailIndexToUsers</c> → <c>add-email-index-to-users</c>.</summary>
    public static string ToKebabCase(string className)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < className.Length; i++)
        {
            var c = className[i];
            var startsWord = i > 0 && char.IsUpper(c) &&
                             (!char.IsUpper(className[i - 1]) || (i + 1 < className.Length && char.IsLower(className[i + 1])));
            var startsNumber = i > 0 && char.IsDigit(c) && !char.IsDigit(className[i - 1]);
            if (startsWord || startsNumber)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    public static string NewId(DateTime utcNow) => utcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>A namespace-safe version of a project name (e.g. <c>My-App.Migrations</c> → <c>My_App.Migrations</c>).</summary>
    public static string ToNamespace(string projectName)
    {
        var parts = projectName.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var cleaned = new string(part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            return char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
        });
        return string.Join('.', parts);
    }
}

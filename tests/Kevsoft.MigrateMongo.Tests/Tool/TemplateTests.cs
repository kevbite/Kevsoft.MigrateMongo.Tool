using Kevsoft.MigrateMongo.Tool;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tool;

/// <summary>
/// The dotnet new templates (src/Kevsoft.MigrateMongo.Templates) must scaffold exactly what <c>migrate-mongo init</c>
/// and <c>migrate-mongo create</c> do; these render the template files the way the template engine would.
/// </summary>
public sealed class TemplateTests
{
    private static readonly string Content = Path.Combine(FindRepositoryRoot(), "src", "Kevsoft.MigrateMongo.Templates", "content");

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net8.0")]
    public void Project_file_matches_init(string framework)
    {
        var template = Read("project", "MigrateMongo.Migrations.csproj");

        var rendered = template.Replace("net10.0", framework).Replace("KEVSOFT_MIGRATEMONGO_VERSION", "1.2.3");

        rendered.ShouldBe(Normalize(Scaffolding.ProjectFile(framework, "1.2.3", ToolInfo.DefaultDriverVersion)));
    }

    [Fact]
    public void Program_matches_init() =>
        Read("project", "Program.cs").ShouldBe(Normalize(Scaffolding.ProgramFile));

    [Fact]
    public void Config_matches_init_for_a_project_in_the_same_directory() =>
        Read("project", "migrate-mongo.yaml").Replace("mydatabase", "shop").ShouldBe(Normalize(Scaffolding.ConfigFile(".", "shop")));

    [Fact]
    public void Migration_matches_create()
    {
        var template = Read("migration", "MMIGRATIONID_MigrationName.cs");

        var rendered = template
            .Replace("MIGRATIONID", "20261013091500")
            .Replace("migration-name", "import-csv-data")
            .Replace("MigrationName", "ImportCSVData")
            .Replace("Company.Migrations", "Shop.Migrations.Migrations");

        rendered.ShouldBe(Normalize(Scaffolding.MigrationFile("Shop.Migrations.Migrations", "20261013091500", "ImportCSVData", "import-csv-data")));
    }

    private static string Read(params string[] parts) => Normalize(File.ReadAllText(Path.Combine([Content, .. parts])));

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kevsoft.MigrateMongo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find the repository root (Kevsoft.MigrateMongo.slnx).");
    }
}

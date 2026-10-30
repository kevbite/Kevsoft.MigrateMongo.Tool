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
    [InlineData("net10.0", false)]
    [InlineData("net11.0", false)]
    [InlineData("net10.0", true)]
    public void Project_file_matches_init(string framework, bool aws)
    {
        var template = RenderAuth(Read("project", "MigrateMongo.Migrations.csproj"), aws);

        var rendered = template.Replace("net10.0", framework).Replace("KEVSOFT_MIGRATEMONGO_VERSION", "1.2.3");

        rendered.ShouldBe(Normalize(Scaffolding.ProjectFile(framework, "1.2.3", ToolInfo.DefaultDriverVersion, aws)));
    }

    [Fact]
    public void Program_matches_init() =>
        Read("project", "Program.cs").ShouldBe(Normalize(Scaffolding.ProgramFile));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Config_matches_init_for_a_project_in_the_same_directory(bool aws) =>
        RenderAuth(Read("project", "migrate-mongo.yaml"), aws).Replace("mydatabase", "shop")
            .ShouldBe(Normalize(Scaffolding.ConfigFile(".", "shop", aws)));

    [Theory]
    [InlineData("migration", false)]
    [InlineData("migration-minimal", true)]
    public void Migration_matches_create(string templateDirectory, bool minimal)
    {
        var template = Read(templateDirectory, "MMIGRATIONID_MigrationName.cs");

        var rendered = template
            .Replace("MIGRATIONID", "20261013091500")
            .Replace("migration-name", "import-csv-data")
            .Replace("MigrationName", "ImportCSVData")
            .Replace("Company.Migrations", "Shop.Migrations.Migrations");

        var create = minimal
            ? Scaffolding.MinimalMigrationFile("Shop.Migrations.Migrations", "20261013091500", "ImportCSVData", "import-csv-data")
            : Scaffolding.MigrationFile("Shop.Migrations.Migrations", "20261013091500", "ImportCSVData", "import-csv-data");
        rendered.ShouldBe(Normalize(create));
    }

    private static string Read(params string[] parts) => Normalize(File.ReadAllText(Path.Combine([Content, .. parts])));

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    // Evaluates the template's `#if (Auth == "aws")` blocks (both the XML-comment and the line-comment forms).
    private static string RenderAuth(string text, bool aws)
    {
        var output = new List<string>();
        var include = true;
        foreach (var line in text.Split('\n'))
        {
            var directive = line.Trim().Replace("<!--", "").Replace("-->", "").Trim();
            if (directive == "#if (Auth == \"aws\")")
            {
                include = aws;
            }
            else if (directive == "#endif")
            {
                include = true;
            }
            else if (include)
            {
                output.Add(line);
            }
        }

        return string.Join('\n', output);
    }

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

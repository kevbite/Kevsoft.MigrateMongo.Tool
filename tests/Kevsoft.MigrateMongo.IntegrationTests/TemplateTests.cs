using System.Text.RegularExpressions;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>The dotnet new templates: install the package, create a project and a migration, then apply it.</summary>
public sealed class TemplateTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Project_and_migration_templates_can_be_applied()
    {
        await using var workspace = Workspace.Empty(environment);
        // A private template store, so nothing is installed into the user's dotnet new.
        var hive = workspace.PathOf("template-hive");
        var package = Path.Combine(environment.PackageFeed, $"Kevsoft.MigrateMongo.Templates.{environment.PackageVersion}.nupkg");
        await DotnetNewAsync(workspace, hive, "install", package);

        var project = await DotnetNewAsync(workspace, hive,
            "migratemongo", "-n", "Shop.Migrations", "-o", "src/Shop.Migrations", "--database-name", workspace.DatabaseName);
        project.Output.ShouldContain("was created successfully");
        var projectFile = File.ReadAllText(workspace.PathOf("src", "Shop.Migrations", "Shop.Migrations.csproj"));
        projectFile.ShouldContain("<TargetFramework>net10.0</TargetFramework>");
        projectFile.ShouldContain($"""<PackageReference Include="Kevsoft.MigrateMongo.Runner" Version="{environment.PackageVersion}" />""");
        File.Exists(workspace.PathOf("src", "Shop.Migrations", "obj", "project.assets.json")).ShouldBeTrue("the template restores the project");
        var config = File.ReadAllText(workspace.PathOf("src", "Shop.Migrations", "migrate-mongo.yaml"));
        config.ShouldContain("project: .");
        config.ShouldContain($"databaseName: {workspace.DatabaseName}");
        config.ShouldContain("url: ${MONGODB_URL}");

        var migration = await DotnetNewAsync(workspace, hive,
            "migratemongo-migration", "-n", "ImportCSVDataV2", "-o", "src/Shop.Migrations/Migrations");
        migration.Output.ShouldNotContain("Failed to evaluate bind symbol");
        var migrationFile = Directory.GetFiles(workspace.PathOf("src", "Shop.Migrations", "Migrations")).ShouldHaveSingleItem();
        var match = Regex.Match(Path.GetFileName(migrationFile), @"^M(\d{14})_ImportCSVDataV2\.cs$");
        match.Success.ShouldBeTrue(migrationFile);
        var id = match.Groups[1].Value;
        var source = File.ReadAllText(migrationFile);
        source.ShouldContain("namespace Shop.Migrations.Migrations;");
        source.ShouldContain($"[Migration(\"{id}\", \"import-csv-data-v-2\")]");
        source.ShouldContain("public sealed class ImportCSVDataV2 : IMigration");
        source.ShouldContain($"// See what ran with `migrate-mongo show {id}`.");

        var notImplemented = "throw new NotImplementedException();";
        var firstThrow = source.IndexOf(notImplemented, StringComparison.Ordinal);
        var upBody = """return context.Database.GetCollection<BsonDocument>("products").InsertOneAsync(new BsonDocument("_id", "widget"), cancellationToken: cancellationToken);""";
        File.WriteAllText(migrationFile, source[..firstThrow] + upBody + source[(firstThrow + notImplemented.Length)..]);

        var up = await workspace.RunAsync(["up", "-f", "src/Shop.Migrations/migrate-mongo.yaml"],
            new Dictionary<string, string?> { ["MONGODB_URL"] = environment.MongoUrl });

        up.ExitCode.ShouldBe(0, up.ToString());
        workspace.Find("products").ShouldBe([new BsonDocument("_id", "widget")]);
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["name"].AsString, s["origin"].AsString))
            .ShouldBe([(id, "import-csv-data-v-2", "Shop.Migrations.Migrations.ImportCSVDataV2")]);

        await DotnetNewAsync(workspace, hive,
            "migratemongo-migration-minimal", "-n", "AddPrices", "-o", "src/Shop.Migrations/Migrations");
        var minimalSource = File.ReadAllText(Directory.GetFiles(workspace.PathOf("src", "Shop.Migrations", "Migrations"), "M*_AddPrices.cs").ShouldHaveSingleItem());
        minimalSource.ShouldContain("namespace Shop.Migrations.Migrations;");
        minimalSource.ShouldContain("public sealed class AddPrices : IMigration");
        minimalSource.ShouldNotContain("context.Track");
    }

    [Fact]
    public async Task Project_template_options()
    {
        await using var workspace = Workspace.Empty(environment);
        var hive = workspace.PathOf("template-hive");
        await DotnetNewAsync(workspace, hive, "install",
            Path.Combine(environment.PackageFeed, $"Kevsoft.MigrateMongo.Templates.{environment.PackageVersion}.nupkg"));

        await DotnetNewAsync(workspace, hive,
            "migratemongo", "-n", "Billing.Migrations", "--framework", "net10.0", "--skip-config", "--no-restore");

        File.ReadAllText(workspace.PathOf("Billing.Migrations", "Billing.Migrations.csproj")).ShouldContain("<TargetFramework>net10.0</TargetFramework>");
        File.Exists(workspace.PathOf("Billing.Migrations", "migrate-mongo.yaml")).ShouldBeFalse();
        Directory.Exists(workspace.PathOf("Billing.Migrations", "obj")).ShouldBeFalse();

        // A root-level config for the template project, as `init` does for an existing project.
        var init = await workspace.RunAsync("init", "--project", "Billing.Migrations");
        init.ExitCode.ShouldBe(0, init.ToString());
        File.ReadAllText(workspace.ConfigPath).ShouldContain("databaseName: billing");
    }

    private async Task<ProcessResult> DotnetNewAsync(Workspace workspace, string hive, params string[] arguments)
    {
        var result = await ProcessHelper.RunAsync("dotnet", ["new", .. arguments, "--debug:custom-hive", hive],
            workspace.Directory, environment.ProcessEnvironment, TimeSpan.FromMinutes(5));
        result.ExitCode.ShouldBe(0, result.ToString());
        return result;
    }
}

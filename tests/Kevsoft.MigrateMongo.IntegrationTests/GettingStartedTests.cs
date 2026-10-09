using System.Text.RegularExpressions;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>The getting-started flow: init → create → write the migration → up.</summary>
public sealed class GettingStartedTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Init_create_and_up()
    {
        await using var workspace = Workspace.Empty(environment);

        var init = await workspace.RunAsync("init", "--project", "src/Shop.Migrations");
        init.ExitCode.ShouldBe(0, init.ToString());
        File.Exists(workspace.PathOf("migrate-mongo.yaml")).ShouldBeTrue();
        var projectFile = File.ReadAllText(workspace.PathOf("src", "Shop.Migrations", "Shop.Migrations.csproj"));
        projectFile.ShouldContain($"""<PackageReference Include="Kevsoft.MigrateMongo.Runner" Version="{environment.PackageVersion}" />""");
        File.ReadAllText(workspace.PathOf("src", "Shop.Migrations", "Program.cs")).ShouldContain("MigrateMongoRunner.RunAsync(args)");

        var config = File.ReadAllText(workspace.ConfigPath);
        config.ShouldContain("project: src/Shop.Migrations");
        config.ShouldContain("url: ${MONGODB_URL}");
        File.WriteAllText(workspace.ConfigPath, config.Replace("databaseName: shop", $"databaseName: {workspace.DatabaseName}"));

        var create = await workspace.RunAsync("create", "AddProducts");
        create.ExitCode.ShouldBe(0, create.ToString());
        var migrationFile = Directory.GetFiles(workspace.PathOf("src", "Shop.Migrations", "Migrations")).ShouldHaveSingleItem();
        var match = Regex.Match(Path.GetFileName(migrationFile), @"^M(\d{14})_AddProducts\.cs$");
        match.Success.ShouldBeTrue(migrationFile);
        var id = match.Groups[1].Value;

        var source = File.ReadAllText(migrationFile);
        source.ShouldContain($"[Migration(\"{id}\", \"add-products\")]");
        source.ShouldContain("namespace Shop.Migrations.Migrations;");
        var upBody = """return context.Database.GetCollection<BsonDocument>("products").InsertOneAsync(new BsonDocument("_id", "widget"), cancellationToken: cancellationToken);""";
        var notImplemented = "throw new NotImplementedException();";
        var firstThrow = source.IndexOf(notImplemented, StringComparison.Ordinal);
        firstThrow.ShouldBeGreaterThan(0);
        File.WriteAllText(migrationFile, source[..firstThrow] + upBody + source[(firstThrow + notImplemented.Length)..]);

        var environmentVariables = new Dictionary<string, string?> { ["MONGODB_URL"] = environment.MongoUrl };
        var up = await workspace.RunAsync(["up"], environmentVariables);

        up.ExitCode.ShouldBe(0, up.ToString());
        workspace.Find("products").ShouldBe([new BsonDocument("_id", "widget")]);
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["name"].AsString, s["origin"].AsString))
            .ShouldBe([(id, "add-products", "Shop.Migrations.Migrations.AddProducts")]);
    }

    [Fact]
    public async Task Version_reports_the_tool_version()
    {
        await using var workspace = Workspace.Empty(environment);

        var version = await workspace.RunAsync("version");

        version.ExitCode.ShouldBe(0, version.ToString());
        version.Output.ShouldStartWith($"migrate-mongo/{environment.PackageVersion} (");
    }
}

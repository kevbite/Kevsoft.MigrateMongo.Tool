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
        source.ShouldContain($"// See what ran with `migrate-mongo show {id}`.");
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
    public async Task Create_uses_a_custom_migration_template()
    {
        await using var workspace = Workspace.Empty(environment);
        var init = await workspace.RunAsync("init", "--project", "src/Shop.Migrations");
        init.ExitCode.ShouldBe(0, init.ToString());

        var config = File.ReadAllText(workspace.ConfigPath).Replace("databaseName: shop", $"databaseName: {workspace.DatabaseName}");
        File.WriteAllText(workspace.ConfigPath, config + "migrationTemplate: templates/migration.cs.template\n");

        var missing = await workspace.RunAsync("create", "AddProducts");
        missing.ExitCode.ShouldNotBe(0);
        (missing.Output + missing.Error).ShouldContain("migration.cs.template");

        Directory.CreateDirectory(workspace.PathOf("templates"));
        File.WriteAllText(workspace.PathOf("templates", "migration.cs.template"), """
            using Kevsoft.MigrateMongo;
            using MongoDB.Bson;
            using MongoDB.Driver;

            namespace {{namespace}};

            // Team template
            [Migration("{{id}}", "{{name}}")]
            public sealed class {{className}} : IMigration
            {
                public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
                    context.Database.GetCollection<BsonDocument>("templated").InsertOneAsync(new BsonDocument("_id", "{{className}}"), cancellationToken: cancellationToken);

                public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """);

        var create = await workspace.RunAsync("create", "AddProducts");
        create.ExitCode.ShouldBe(0, create.ToString());
        var migrationFile = Directory.GetFiles(workspace.PathOf("src", "Shop.Migrations", "Migrations")).ShouldHaveSingleItem();
        var id = Regex.Match(Path.GetFileName(migrationFile), @"^M(\d{14})_AddProducts\.cs$").Groups[1].Value;
        var source = File.ReadAllText(migrationFile);
        source.ShouldContain("// Team template");
        source.ShouldContain("namespace Shop.Migrations.Migrations;");
        source.ShouldContain($"[Migration(\"{id}\", \"add-products\")]");

        var up = await workspace.RunAsync(["up"], new Dictionary<string, string?> { ["MONGODB_URL"] = environment.MongoUrl });

        up.ExitCode.ShouldBe(0, up.ToString());
        workspace.Find("templated").ShouldBe([new BsonDocument("_id", "AddProducts")]);

        // --minimal skips the custom template.
        var minimal = await workspace.RunAsync("create", "AddPrices", "--minimal");
        minimal.ExitCode.ShouldBe(0, minimal.ToString());
        var minimalSource = File.ReadAllText(Directory.GetFiles(workspace.PathOf("src", "Shop.Migrations", "Migrations"), "M*_AddPrices.cs").ShouldHaveSingleItem());
        minimalSource.ShouldNotContain("// Team template");
        minimalSource.ShouldNotContain("context.Track");
        minimalSource.ShouldContain("public sealed class AddPrices : IMigration");
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

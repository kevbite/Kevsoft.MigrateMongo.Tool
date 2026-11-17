using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>JavaScript migrations and collection definitions, run by the installed tool with no .NET project.</summary>
public sealed class ScriptMigrationTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Up_applies_script_migrations_and_collection_definitions()
    {
        await using var workspace = ScriptWorkspace();

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain($"Runner: Kevsoft.MigrateMongo {environment.PackageVersion}");
        up.Output.ShouldContain("info: normalised 2 of 3");
        up.Output.ShouldContain("Applied 3 migration(s) in batch 1 and changed 2 collection(s).");

        var states = workspace.Find("_migrations");
        states.Select(s => (s["_id"].AsString, s["name"].AsString, s["origin"].AsString, s["source"].AsString, s["transactional"].AsBoolean)).ShouldBe([
            ("20260201000001", "create-users", "migrations/20260201000001-create-users.js", "js", false),
            ("20260201000002", "seed-roles", "migrations/20260201000002-seed-roles.js", "js", true),
            ("20260201000003", "normalise-emails", "migrations/20260201000003-normalise-emails.js", "js", true),
        ]);
        states.ShouldAllBe(s => Regex.IsMatch(s["checksum"].AsString, "^sha256:[0-9a-f]{64}$"));
        states[2]["results"]["normalised"].ShouldBe(new BsonInt32(2));

        workspace.Find("users").ShouldBe([
            BsonDocument.Parse("""{ "_id": "ann", "email": "ann@example.com", "roles": ["admin"], "previousEmail": "ANN@Example.com" }"""),
            BsonDocument.Parse("""{ "_id": "bob", "email": "bob@example.com", "roles": ["reader"], "previousEmail": "bob@example.com " }"""),
            BsonDocument.Parse("""{ "_id": "cat", "email": "cat@example.com", "roles": ["reader"] }"""),
        ]);
        workspace.Find("roles").Select(r => r["_id"].AsString).ShouldBe(["admin", "reader"]);
        var userIndex = workspace.Database.GetCollection<BsonDocument>("users").Indexes.List().ToList().Single(i => i["name"] == "email_unique");
        userIndex["unique"].AsBoolean.ShouldBeTrue();

        var orders = workspace.Database.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", "orders") }).Single();
        orders["options"]["validator"].ShouldBe(BsonDocument.Parse("""{ "$jsonSchema": { "bsonType": "object", "required": ["customerId", "reference"] } }"""));
        orders["options"]["validationLevel"].AsString.ShouldBe("moderate");
        var orderIndexes = workspace.Database.GetCollection<BsonDocument>("orders").Indexes.List().ToList().ToDictionary(i => i["name"].AsString);
        orderIndexes.Keys.Order(StringComparer.Ordinal).ShouldBe(["_id_", "customerId_1_createdAt_-1", "expiresAt_1", "reference_unique"]);
        orderIndexes["expiresAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(30 * 24 * 60 * 60);
        orderIndexes["reference_unique"]["unique"].AsBoolean.ShouldBeTrue();

        var openOrders = workspace.Database.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", "open_orders") }).Single();
        openOrders["type"].AsString.ShouldBe("view");
        openOrders["options"]["viewOn"].AsString.ShouldBe("orders");
        openOrders["options"]["pipeline"].ShouldBe(BsonArray.Create(new[]
        {
            BsonDocument.Parse("""{ "$match": { "status": "open" } }"""),
            BsonDocument.Parse("""{ "$project": { "reference": 1, "customerId": 1 } }"""),
        }));

        var status = await workspace.RunAsync("status");
        status.ExitCode.ShouldBe(0, status.ToString());
        status.Output.ShouldContain("Database is up to date.");
    }

    [Fact]
    public async Task Show_lists_what_a_script_did()
    {
        await using var workspace = ScriptWorkspace();
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var show = await workspace.RunAsync("show", "20260201000003");

        show.ExitCode.ShouldBe(0, show.ToString());
        show.Output.ShouldContain("Source    js migrations/20260201000003-normalise-emails.js");
        show.Output.ShouldContain("info     Checked 3 users.");
        show.Output.ShouldContain("1. bulkWrite users [normalise]  inserted 0 · matched 2 · modified 2 · deleted 0 · upserted 0");
        show.Output.ShouldContain("""{ kind: "updateOne", filter: { _id: "ann" }, update: { $set: { email: "ann@example.com", previousEmail: "ANN@Example.com" } } }""");
    }

    [Fact]
    public async Task Down_rolls_the_scripts_back()
    {
        await using var workspace = ScriptWorkspace();
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var down = await workspace.RunAsync("down", "--batch");

        down.ExitCode.ShouldBe(0, down.ToString());
        down.Output.ShouldContain("    restored  2");
        workspace.Find("_migrations").ShouldBeEmpty();
        workspace.Find("_migrations.history", sortBy: "at").Where(h => h["event"] != "reconciled").Select(h => (h["migrationId"].AsString, h["event"].AsString)).ShouldBe([
            ("20260201000001", "applied"),
            ("20260201000002", "applied"),
            ("20260201000003", "applied"),
            ("20260201000003", "rolledBack"),
            ("20260201000002", "rolledBack"),
            ("20260201000001", "rolledBack"),
        ]);
        workspace.CollectionNames().ShouldNotContain("users");
        workspace.Find("roles").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_script_rolls_its_transaction_back_and_reports_where()
    {
        await using var workspace = Workspace.Empty(environment);
        WriteScripts(workspace, "scripts", ("migrations/20260301000001-seed.js", """
            export async function up(db) {
              await db.collection('products').insertOne({ _id: 'widget' });
              await db.collection('products').insertOne({ _id: 'widget' });
            }
            """));
        WriteScriptConfig(workspace, "scripts");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldNotBe(0, up.ToString());
        (up.Output + up.Error).ShouldContain("E11000 duplicate key error");
        (up.Output + up.Error).ShouldContain("transaction aborted – changes not persisted");
        workspace.Find("products").ShouldBeEmpty();
        workspace.Find("_migrations").ShouldBeEmpty();
        var failed = workspace.Find("_migrations.history").ShouldHaveSingleItem();
        failed["event"].AsString.ShouldBe("failed");
        // Driver errors reach the engine as the driver's own exception, as they would from a C# migration.
        failed["error"].AsString.ShouldStartWith("MongoWriteException: ");
        failed["error"].AsString.ShouldContain("E11000 duplicate key error");
    }

    [Fact]
    public async Task A_script_error_points_at_the_file_and_line()
    {
        await using var workspace = Workspace.Empty(environment);
        WriteScripts(workspace, "scripts", ("migrations/20260301000001-broken.js", """
            export async function up(db) {
              const user = await db.collection('users').findOne({ _id: 'missing' });
              user.email.toLowerCase();
            }
            """));
        WriteScriptConfig(workspace, "scripts");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldNotBe(0, up.ToString());
        (up.Output + up.Error).ShouldContain("migrations/20260301000001-broken.js: TypeError:");
        (up.Output + up.Error).ShouldContain("migrations/20260301000001-broken.js:3");
    }

    [Fact]
    public async Task Csharp_and_javascript_migrations_run_in_one_timeline()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        WriteScripts(workspace, "scripts",
            ("migrations/20260101000004-tag-admins.js", """
                export async function up(db) {
                  await db.collection('users').updateMany({ roles: 'admin' }, { $set: { tagged: true } });
                }
                """),
            ("collections/audit.js", """
                import { collection } from '@kevsoft/migrate-mongo';
                export default collection('audit', c => c.indexes.add({ at: 1 }));
                """));
        File.AppendAllText(workspace.ConfigPath, $"{Environment.NewLine}scripts: scripts{Environment.NewLine}");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["source"].AsString)).ShouldBe([
            ("20260101000001", "csharp"),
            ("20260101000002", "csharp"),
            ("20260101000003", "csharp"),
            ("20260101000004", "js"),
        ]);
        workspace.Find("users").ShouldHaveSingleItem()["tagged"].AsBoolean.ShouldBeTrue();
        workspace.Database.GetCollection<BsonDocument>("audit").Indexes.List().ToList().Select(i => i["name"].AsString).ShouldContain("at_1");

        var create = await workspace.RunAsync("create", "AddAuditTrail", "--js");
        create.ExitCode.ShouldBe(0, create.ToString());
        Directory.GetFiles(workspace.PathOf("scripts", "migrations"), "*-add-audit-trail.js").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Init_js_create_and_up()
    {
        await using var workspace = Workspace.Empty(environment);

        var init = await workspace.RunAsync("init", "--js", "--project", "db");

        init.ExitCode.ShouldBe(0, init.ToString());
        var config = File.ReadAllText(workspace.ConfigPath);
        config.ShouldContain("scripts: db");
        config.ShouldNotContain("project:");
        Directory.Exists(workspace.PathOf("db", "migrations")).ShouldBeTrue();
        Directory.Exists(workspace.PathOf("db", "collections")).ShouldBeTrue();
        File.ReadAllText(workspace.PathOf("db", "types", "migrate-mongo.d.ts")).ShouldContain("declare module '@kevsoft/migrate-mongo'");
        File.ReadAllText(workspace.PathOf("db", "jsconfig.json")).ShouldContain("\"checkJs\": true");
        File.ReadAllText(workspace.PathOf("db", "package.json")).ShouldContain("\"typecheck\": \"tsc -p jsconfig.json\"");
        File.WriteAllText(workspace.ConfigPath, config.Replace("databaseName: db", $"databaseName: {workspace.DatabaseName}"));
        var environmentVariables = new Dictionary<string, string?> { ["MONGODB_URL"] = environment.MongoUrl };

        var empty = await workspace.RunAsync(["status"], environmentVariables);
        empty.ExitCode.ShouldBe(0, empty.ToString());
        empty.Output.ShouldContain("No migrations or collection definitions were found in");

        var create = await workspace.RunAsync("create", "AddProducts");
        create.ExitCode.ShouldBe(0, create.ToString());
        var migrationFile = Directory.GetFiles(workspace.PathOf("db", "migrations")).ShouldHaveSingleItem();
        var match = Regex.Match(Path.GetFileName(migrationFile), @"^(\d{14})-add-products\.js$");
        match.Success.ShouldBeTrue(migrationFile);
        var source = File.ReadAllText(migrationFile);
        source.ShouldContain("import { defineMigration } from '@kevsoft/migrate-mongo';");
        File.WriteAllText(migrationFile, source.Replace(
            "throw new Error('Not implemented yet.');",
            "await db.collection('products').insertOne({ _id: 'widget' }); return;"));

        var up = await workspace.RunAsync(["up"], environmentVariables);

        up.ExitCode.ShouldBe(0, up.ToString());
        workspace.Find("products").ShouldBe([new BsonDocument("_id", "widget")]);
        workspace.Find("_migrations").ShouldHaveSingleItem()["_id"].AsString.ShouldBe(match.Groups[1].Value);
    }

    [Fact]
    public async Task Scaffolded_scripts_type_check_with_typescript()
    {
        Assert.SkipWhen(FindOnPath("npm") is null, "npm isn't installed.");
        await using var workspace = Workspace.Empty(environment);
        (await workspace.RunAsync("init", "--js")).ExitCode.ShouldBe(0);
        (await workspace.RunAsync("create", "AddProducts")).ExitCode.ShouldBe(0);
        WriteScripts(workspace, ".", ("collections/orders.js", """
            // @ts-check
            import { collection, view } from '@kevsoft/migrate-mongo';
            export const orders = collection('orders', c => {
              c.indexes.add({ reference: 1 }, { unique: true }).prune = 'drop';
              c.options.capped({ maxSize: 1024 * 1024 });
            });
            export const open = view('open_orders', { on: 'orders' }, v => v.pipeline([{ $match: { open: true } }]));
            """));

        var npm = FindOnPath("npm")!;
        var install = await ProcessHelper.RunAsync(npm, ["install", "--no-audit", "--no-fund"], workspace.Directory, null, TimeSpan.FromMinutes(5));
        install.ExitCode.ShouldBe(0, install.ToString());
        var check = await ProcessHelper.RunAsync(npm, ["run", "typecheck"], workspace.Directory, null, TimeSpan.FromMinutes(2));
        check.ExitCode.ShouldBe(0, check.ToString());

        WriteScripts(workspace, ".", ("migrations/20990101000000-mistakes.js", """
            // @ts-check
            import { defineMigration } from '@kevsoft/migrate-mongo';
            export default defineMigration({
              async up(db, context) {
                context.results.sett('x', 1);
                (await db.collection('users').countDocuments()).toUpperCase();
              },
            });
            """));
        var mistakes = await ProcessHelper.RunAsync(npm, ["run", "typecheck"], workspace.Directory, null, TimeSpan.FromMinutes(2));

        mistakes.ExitCode.ShouldNotBe(0);
        mistakes.Output.ShouldContain("Property 'sett' does not exist on type 'MigrationResults'");
        mistakes.Output.ShouldContain("Property 'toUpperCase' does not exist on type 'number'");
    }

    [Fact]
    public async Task Bundle_says_scripts_dont_need_one()
    {
        await using var workspace = ScriptWorkspace();

        var bundle = await workspace.RunAsync("bundle");

        bundle.ExitCode.ShouldNotBe(0);
        (bundle.Output + bundle.Error).ShouldContain("JavaScript migrations run with the tool itself");
    }

    private Workspace ScriptWorkspace()
    {
        var workspace = Workspace.Empty(environment);
        workspace.AddFixture("ScriptMigrations");
        WriteScriptConfig(workspace, "ScriptMigrations");
        return workspace;
    }

    private void WriteScriptConfig(Workspace workspace, string scripts) =>
        workspace.WriteConfigFile($"""
            scripts: {scripts}
            mongodb:
              url: {environment.MongoUrl}
              databaseName: {workspace.DatabaseName}
            lockWaitTimeout: 5
            """);

    private static void WriteScripts(Workspace workspace, string root, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = workspace.PathOf([root, .. path.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static string? FindOnPath(string command)
    {
        var names = OperatingSystem.IsWindows() ? new[] { command + ".cmd", command + ".exe" } : [command];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}

using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>Applying and rolling back the BasicMigrations fixture.</summary>
public sealed class UpAndDownTests(IntegrationEnvironment environment)
{
    private const string Checksum = @"^sha256:[0-9a-f]{64}$";

    [Fact]
    public async Task Up_applies_every_migration_and_records_state_history_and_data()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("20260101000001");
        up.Output.ShouldContain("20260101000003");

        workspace.CollectionNames().ShouldBe(["_migrations", "_migrations.history", "_migrations.lock", "roles", "users"]);

        var states = workspace.Find("_migrations");
        states.Select(s => s["_id"].AsString).ShouldBe(["20260101000001", "20260101000002", "20260101000003"]);
        states.Select(s => s["name"].AsString).ShouldBe(["create-users", "users-email-index", "seed-defaults"]);
        states.Select(s => s["origin"].AsString).ShouldBe([
            "BasicMigrations.Migrations.CreateUsers",
            "BasicMigrations.Migrations.UsersEmailIndex",
            "BasicMigrations.Migrations.SeedDefaults",
        ]);
        states.Select(s => s["transactional"].AsBoolean).ShouldBe([false, false, true]);
        var runId = states[0]["runId"].AsString;
        runId.ShouldMatch("^[0-9a-f]{32}$");
        foreach (var state in states)
        {
            state["v"].AsInt32.ShouldBe(1);
            state["source"].AsString.ShouldBe("csharp");
            state["checksum"].AsString.ShouldMatch(Checksum);
            state["state"].AsString.ShouldBe("applied");
            state["direction"].AsString.ShouldBe("up");
            state["batch"].AsInt32.ShouldBe(1);
            state["runId"].AsString.ShouldBe(runId);
            state["appliedAt"].ToUniversalTime().ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(5));
            state.Contains("error").ShouldBeFalse();

            var appliedBy = state["appliedBy"].AsBsonDocument;
            appliedBy["host"].AsString.ShouldBe(Environment.MachineName);
            appliedBy["tool"].AsString.ShouldStartWith($"migrate-mongo/{environment.PackageVersion} (");
            appliedBy["library"].AsString.ShouldBe(environment.PackageVersion);
            appliedBy["driver"].AsString.ShouldBe("3.12.0");
            appliedBy["runtime"].AsString.ShouldStartWith("10.0.");
        }

        var history = workspace.Find("_migrations.history", sortBy: "at");
        history.Select(h => (h["migrationId"].AsString, h["event"].AsString, h["direction"].AsString, h["batch"].AsInt32)).ShouldBe([
            ("20260101000001", "applied", "up", 1),
            ("20260101000002", "applied", "up", 1),
            ("20260101000003", "applied", "up", 1),
        ]);
        history.ShouldAllBe(h => h["runId"].AsString == runId);

        workspace.Find("_migrations.lock").ShouldBeEmpty();

        var users = workspace.Database.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", "users") }).Single();
        users["options"]["validator"].ShouldBe(BsonDocument.Parse("""{ "$jsonSchema": { "bsonType": "object", "required": ["email"] } }"""));
        var emailIndex = workspace.Database.GetCollection<BsonDocument>("users").Indexes.List().ToList().Single(i => i["name"] == "email_1");
        emailIndex["key"].ShouldBe(new BsonDocument("email", 1));
        emailIndex["unique"].AsBoolean.ShouldBeTrue();

        workspace.Find("roles").ShouldBe([
            BsonDocument.Parse("""{ "_id": "admin", "permissions": ["read", "write"] }"""),
            BsonDocument.Parse("""{ "_id": "reader", "permissions": ["read"] }"""),
        ]);
        workspace.Find("users").ShouldBe([BsonDocument.Parse("""{ "_id": "admin", "email": "admin@example.com", "roles": ["admin"] }""")]);
    }

    [Fact]
    public async Task Up_then_down_batch_restores_the_database()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var down = await workspace.RunAsync("down", "--batch");

        down.ExitCode.ShouldBe(0, down.ToString());
        workspace.Find("_migrations").ShouldBeEmpty();
        workspace.Find("_migrations.lock").ShouldBeEmpty();
        workspace.Find("_migrations.history", sortBy: "at").Select(h => (h["migrationId"].AsString, h["event"].AsString, h["direction"].AsString)).ShouldBe([
            ("20260101000001", "applied", "up"),
            ("20260101000002", "applied", "up"),
            ("20260101000003", "applied", "up"),
            ("20260101000003", "rolledBack", "down"),
            ("20260101000002", "rolledBack", "down"),
            ("20260101000001", "rolledBack", "down"),
        ]);
        workspace.CollectionNames().ShouldBe(["_migrations", "_migrations.history", "_migrations.lock", "roles"]);
        workspace.Find("roles").ShouldBeEmpty();

        var again = await workspace.RunAsync("up");
        again.ExitCode.ShouldBe(0, again.ToString());
        workspace.Find("_migrations").ShouldAllBe(s => s["batch"].AsInt32 == 1 && s["state"].AsString == "applied");
        workspace.Find("_migrations").Count.ShouldBe(3);
    }

    [Fact]
    public async Task Down_to_rolls_back_only_newer_migrations()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var down = await workspace.RunAsync("down", "--to", "20260101000001");

        down.ExitCode.ShouldBe(0, down.ToString());
        workspace.Find("_migrations").Select(s => s["_id"].AsString).ShouldBe(["20260101000001"]);
        workspace.Find("users").ShouldBeEmpty();
        workspace.Database.GetCollection<BsonDocument>("users").Indexes.List().ToList().Select(i => i["name"].AsString).ShouldBe(["_id_"]);
    }

    [Fact]
    public async Task Plan_and_status_report_pending_work_through_exit_codes()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");

        var plan = await workspace.RunJsonAsync("plan");
        plan.Process.ExitCode.ShouldBe(2, plan.Process.ToString());
        plan.Result.GetProperty("plan").EnumerateArray().Select(p => (p.GetProperty("id").GetString(), p.GetProperty("transactional").GetBoolean())).ShouldBe([
            ("20260101000001", false),
            ("20260101000002", false),
            ("20260101000003", true),
        ]);
        workspace.Find("_migrations").ShouldBeEmpty();

        (await workspace.RunAsync("status", "--check", "--no-build")).ExitCode.ShouldBe(2);
        (await workspace.RunAsync("validate", "--no-build")).ExitCode.ShouldBe(0);
        (await workspace.RunAsync("history", "--no-build")).ExitCode.ShouldBe(0);
        // Reading commands don't create collections or indexes, so they work with a read-only user.
        workspace.CollectionNames().ShouldBeEmpty();
        (await workspace.RunAsync("up", "--to", "20260101000002", "--no-build")).ExitCode.ShouldBe(0);

        var status = await workspace.RunJsonAsync("status", "--no-build");
        status.Process.ExitCode.ShouldBe(0, status.Process.ToString());
        status.Result.GetProperty("migrations").EnumerateArray().Select(m => (m.GetProperty("id").GetString(), m.GetProperty("state").GetString())).ShouldBe([
            ("20260101000001", "applied"),
            ("20260101000002", "applied"),
            ("20260101000003", "pending"),
        ]);

        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(0);
        (await workspace.RunAsync("status", "--check", "--no-build")).ExitCode.ShouldBe(0);
        (await workspace.RunAsync("plan", "--no-build")).ExitCode.ShouldBe(0);

        var history = await workspace.RunJsonAsync("history", "--no-build");
        history.Result.GetProperty("history").EnumerateArray().Select(h => (h.GetProperty("migrationId").GetString(), h.GetProperty("batch").GetInt32()))
            .ShouldBe([("20260101000001", 1), ("20260101000002", 1), ("20260101000003", 2)]);
    }

    [Fact]
    public async Task Runner_can_be_run_standalone_without_the_tool()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("plan")).ExitCode.ShouldBe(2);
        var runner = workspace.PathOf("BasicMigrations", "bin", "Debug", "net10.0", "BasicMigrations.dll");

        var up = await ProcessHelper.RunAsync("dotnet", [runner, "up"], workspace.Directory, environment.ProcessEnvironment, TimeSpan.FromMinutes(2));

        up.ExitCode.ShouldBe(0, up.ToString());
        workspace.Find("_migrations").Count.ShouldBe(3);
        workspace.Find("_migrations").ShouldAllBe(s => !s["appliedBy"].AsBsonDocument.Contains("tool"));
    }
}

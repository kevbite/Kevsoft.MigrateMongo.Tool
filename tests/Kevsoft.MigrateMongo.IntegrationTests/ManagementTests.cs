using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

public sealed class ManagementTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task State_reads_omit_recorded_operations_unless_requested()
    {
        var databaseName = "state_projection_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            await store.SaveStateAsync(new StoredMigrationState
            {
                Id = "20260101000001",
                Name = "large-output",
                State = MigrationStates.Applied,
                Batch = 1,
                Output = new MigrationOutputInfo
                {
                    Committed = true,
                    Operations = [new OperationRecord { Kind = "insertMany", Collection = "large", Label = "seed" }],
                },
            }, null, CancellationToken.None);

            var statusState = (await store.GetStatesAsync(CancellationToken.None)).Single();
            statusState.Output!.Committed.ShouldBe(true);
            statusState.Output.Operations.ShouldBeNull();

            var showState = (await store.GetStatesAsync(CancellationToken.None, includeOperations: true)).Single();
            showState.Output!.Operations!.Select(o => (o.Kind, o.Collection, o.Label))
                .ShouldBe([("insertMany", "large", "seed")]);
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Lock_held_by_another_run_times_out_and_can_be_forced_open()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations", lockWaitTimeout: 1);
        await workspace.Database.GetCollection<BsonDocument>("_migrations.lock").InsertOneAsync(new BsonDocument
        {
            { "_id", "lock" },
            { "runId", "someone-else" },
            { "host", "elsewhere" },
            { "acquiredAt", DateTime.UtcNow },
            { "expiresAt", DateTime.UtcNow.AddMinutes(10) },
            { "heartbeatAt", DateTime.UtcNow },
        });

        var up = await workspace.RunAsync("up");
        up.ExitCode.ShouldBe(3, up.ToString());
        workspace.Find("_migrations").ShouldBeEmpty();

        (await workspace.RunAsync("unlock", "--no-build")).ExitCode.ShouldBe(1);
        workspace.Find("_migrations.lock").Single()["runId"].AsString.ShouldBe("someone-else");

        (await workspace.RunAsync("unlock", "--force", "--no-build")).ExitCode.ShouldBe(0);
        workspace.Find("_migrations.lock").ShouldBeEmpty();

        var afterUnlock = await workspace.RunAsync("up", "--no-build");
        afterUnlock.ExitCode.ShouldBe(0, afterUnlock.ToString());
    }

    [Fact]
    public async Task Lock_acquisition_renewal_release_and_unlock_work_on_the_current_topology()
    {
        var databaseName = "lock_lifecycle_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("first", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            (await store.RenewLockAsync("first", TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBeTrue();
            (await store.TryAcquireLockAsync("second", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBeNull();
            await store.ReleaseLockAsync("first", CancellationToken.None);
            (await store.GetLockAsync(CancellationToken.None)).ShouldBeNull();

            (await store.TryAcquireLockAsync("second", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            (await store.DeleteLockAsync(onlyIfExpired: false, CancellationToken.None)).ShouldBeTrue();
            (await database.GetCollection<BsonDocument>("_migrations.lock").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ShouldBeEmpty();
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Transactional_lock_fence_rejects_a_stale_owner()
    {
        var databaseName = "lock_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("stale-owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            (await store.DeleteLockAsync(onlyIfExpired: false, CancellationToken.None)).ShouldBeTrue();
            (await store.TryAcquireLockAsync("current-owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();

            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using var scope = new MongoExecutionScope(session);

            (await store.FenceLockAsync("stale-owner", scope, CancellationToken.None)).ShouldBeFalse();
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Transactional_lock_fence_rejects_an_expired_owner()
    {
        var databaseName = "expired_lock_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("expired-owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            await database.GetCollection<BsonDocument>("_migrations.lock").UpdateOneAsync(
                new BsonDocument("runId", "expired-owner"),
                new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))));

            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using var scope = new MongoExecutionScope(session);

            (await store.FenceLockAsync("expired-owner", scope, CancellationToken.None)).ShouldBeFalse();
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Transactional_fence_and_state_insert_commit_together()
    {
        var databaseName = "transactional_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("current-owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();

            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using (var scope = new MongoExecutionScope(session))
            {
                (await store.FenceLockAsync("current-owner", scope, CancellationToken.None)).ShouldBeTrue();
                await store.SaveStateAsync(new StoredMigrationState
                {
                    Id = "20260101000001",
                    State = MigrationStates.Applied,
                    RunId = "current-owner",
                    Batch = 1,
                }, scope, CancellationToken.None);
                await scope.CommitAsync(CancellationToken.None);
            }

            var saved = await database.GetCollection<BsonDocument>("_migrations")
                .Find(new BsonDocument("_id", "20260101000001"))
                .SingleAsync();
            saved["runId"].AsString.ShouldBe("current-owner");
            (await database.GetCollection<BsonDocument>("_migrations.lock").Find(new BsonDocument("_id", "fence")).SingleAsync())
                .Contains("fencedAt").ShouldBeTrue();
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Heartbeat_after_transaction_snapshot_does_not_conflict_with_fencing()
    {
        var databaseName = "heartbeat_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            await database.GetCollection<BsonDocument>("_migrations.lock").UpdateOneAsync(
                new BsonDocument("_id", "lock"),
                new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))));

            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using (var scope = new MongoExecutionScope(session))
            {
                await store.SaveStateAsync(new StoredMigrationState
                {
                    Id = "20260101000001",
                    State = MigrationStates.Applied,
                    RunId = "owner",
                    Batch = 1,
                }, scope, CancellationToken.None);

                (await store.RenewLockAsync("owner", TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldBeTrue();
                (await store.FenceLockAsync("owner", scope, CancellationToken.None)).ShouldBeTrue();
                await scope.CommitAsync(CancellationToken.None);
            }

            var saved = await database.GetCollection<BsonDocument>("_migrations")
                .Find(new BsonDocument("_id", "20260101000001")).SingleAsync();
            saved["runId"].AsString.ShouldBe("owner");
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Takeover_or_forced_unlock_invalidates_a_transaction_snapshot(bool forceUnlock)
    {
        var databaseName = "invalidated_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("old-owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using (var scope = new MongoExecutionScope(session))
            {
                await store.SaveStateAsync(new StoredMigrationState
                {
                    Id = "20260101000001",
                    State = MigrationStates.Applied,
                    RunId = "old-owner",
                    Batch = 1,
                }, scope, CancellationToken.None);

                if (forceUnlock)
                {
                    (await store.DeleteLockAsync(onlyIfExpired: false, CancellationToken.None)).ShouldBeTrue();
                }
                else
                {
                    await database.GetCollection<BsonDocument>("_migrations.lock").UpdateOneAsync(
                        new BsonDocument("_id", "lock"),
                        new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))));
                    (await store.TryAcquireLockAsync("new-owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
                }

                (await store.FenceLockAsync("old-owner", scope, CancellationToken.None)).ShouldBeFalse();
            }

            (await store.GetStatesAsync(CancellationToken.None)).ShouldBeEmpty();
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Ownership_fence_conflicts_when_changed_after_the_transaction_snapshot()
    {
        var databaseName = "conflicting_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            (await store.TryAcquireLockAsync("owner", null, TimeSpan.FromMinutes(1), CancellationToken.None)).ShouldNotBeNull();
            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using var scope = new MongoExecutionScope(session);
            await store.SaveStateAsync(new StoredMigrationState
            {
                Id = "20260101000001",
                State = MigrationStates.Applied,
                RunId = "owner",
                Batch = 1,
            }, scope, CancellationToken.None);

            await database.GetCollection<BsonDocument>("_migrations.lock").UpdateOneAsync(
                new BsonDocument("_id", "fence"),
                new BsonDocument("$set", new BsonDocument("runId", "new-owner")));

            var error = await Should.ThrowAsync<MongoException>(() => store.FenceLockAsync("owner", scope, CancellationToken.None));
            error.HasErrorLabel("TransientTransactionError").ShouldBeTrue();
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Takeover_or_unlock_waits_for_a_fenced_transaction_to_finish(bool forceUnlock)
    {
        var databaseName = "locked_fence_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await store.InitializeAsync(timeout.Token);
            (await store.TryAcquireLockAsync("old-owner", null, TimeSpan.FromMinutes(1), timeout.Token)).ShouldNotBeNull();
            var session = await environment.Client.StartSessionAsync(cancellationToken: timeout.Token);
            session.StartTransaction();
            await using (var scope = new MongoExecutionScope(session))
            {
                (await store.FenceLockAsync("old-owner", scope, timeout.Token)).ShouldBeTrue();
                if (!forceUnlock)
                {
                    await database.GetCollection<BsonDocument>("_migrations.lock").UpdateOneAsync(
                        new BsonDocument("_id", "lock"),
                        new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))),
                        cancellationToken: timeout.Token);
                }

                var invalidation = forceUnlock
                    ? InvalidateByUnlockAsync()
                    : InvalidateByTakeoverAsync();
                (await Task.WhenAny(invalidation, Task.Delay(300, timeout.Token))).ShouldNotBeSameAs(invalidation);

                await scope.CommitAsync(timeout.Token);
                (await invalidation).ShouldBeTrue();
            }

            async Task<bool> InvalidateByUnlockAsync() =>
                await store.DeleteLockAsync(onlyIfExpired: false, timeout.Token);

            async Task<bool> InvalidateByTakeoverAsync() =>
                await store.TryAcquireLockAsync("new-owner", null, TimeSpan.FromMinutes(1), timeout.Token) is not null;
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_lock_acquisition_has_only_one_owner()
    {
        var databaseName = "concurrent_lock_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        try
        {
            var stores = Enumerable.Range(0, 8).Select(_ => new MongoMigrationStore(database, "_migrations")).ToArray();
            await stores[0].InitializeAsync(CancellationToken.None);

            var results = await Task.WhenAll(stores.Select((store, index) =>
                store.TryAcquireLockAsync($"owner-{index}", null, TimeSpan.FromMinutes(1), CancellationToken.None)));

            results.Count(result => result is not null).ShouldBe(1);
            var owner = results.Single(result => result is not null)!.RunId;
            var documents = await database.GetCollection<BsonDocument>("_migrations.lock")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            documents.Single(d => d["_id"] == "lock")["runId"].AsString.ShouldBe(owner);
            documents.Single(d => d["_id"] == "fence")["runId"].AsString.ShouldBe(owner);
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Pending_state_insert_does_not_replace_an_existing_state()
    {
        var databaseName = "state_insert_" + Guid.NewGuid().ToString("N");
        var database = environment.Client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, "_migrations");
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            var original = new StoredMigrationState
            {
                Id = "20260101000001",
                State = MigrationStates.Applying,
                RunId = "first-run",
                Batch = 1,
            };
            await store.SaveStateAsync(original, null, CancellationToken.None);

            var conflict = new StoredMigrationState
            {
                Id = original.Id,
                State = MigrationStates.Applying,
                RunId = "second-run",
                Batch = 2,
            };

            await Should.ThrowAsync<MongoWriteException>(() => store.SaveStateAsync(conflict, null, CancellationToken.None));

            var saved = await database.GetCollection<BsonDocument>("_migrations")
                .Find(new BsonDocument("_id", original.Id))
                .SingleAsync();
            saved["runId"].AsString.ShouldBe("first-run");
            saved["batch"].AsInt32.ShouldBe(1);

            var session = await environment.Client.StartSessionAsync();
            session.StartTransaction();
            await using var scope = new MongoExecutionScope(session);
            var transactionalConflict = new StoredMigrationState
            {
                Id = original.Id,
                State = MigrationStates.Applied,
                RunId = "transactional-run",
                Batch = 3,
            };

            await Should.ThrowAsync<MongoWriteException>(() =>
                store.SaveStateAsync(transactionalConflict, scope, CancellationToken.None));
        }
        finally
        {
            await environment.Client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Baseline_records_existing_migrations_without_running_them()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");

        var baseline = await workspace.RunAsync("baseline", "--to", "20260101000002");

        baseline.ExitCode.ShouldBe(0, baseline.ToString());
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["state"].AsString, s["batch"].AsInt32))
            .ShouldBe([("20260101000001", "applied", 1), ("20260101000002", "applied", 1)]);
        workspace.Find("_migrations.history", sortBy: "at").Select(h => h["event"].AsString).ShouldBe(["baselined", "baselined"]);
        workspace.CollectionNames().ShouldNotContain("users");

        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(0);
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["batch"].AsInt32))
            .ShouldBe([("20260101000001", 1), ("20260101000002", 1), ("20260101000003", 2)]);
    }

    [Fact]
    public async Task Mark_unapplied_makes_a_migration_pending_again()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var mark = await workspace.RunAsync("mark", "20260101000003", "--unapplied", "--no-build");

        mark.ExitCode.ShouldBe(0, mark.ToString());
        workspace.Find("_migrations").Select(s => s["_id"].AsString).ShouldBe(["20260101000001", "20260101000002"]);
        workspace.Find("_migrations.history", sortBy: "at").Last()["event"].AsString.ShouldBe("markedUnapplied");
        workspace.Find("users").Count.ShouldBe(1, "marking doesn't run the migration's down");
    }

    [Fact]
    public async Task Validate_detects_code_changes_but_ignores_formatting()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);
        var file = workspace.PathOf("BasicMigrations", "Migrations", "M20260101000002_UsersEmailIndex.cs");
        var original = File.ReadAllText(file);

        File.WriteAllText(file, "// A comment that doesn't change anything.\n" + original.Replace("    ", "\t"));
        var reformatted = await workspace.RunAsync("validate");
        reformatted.ExitCode.ShouldBe(0, reformatted.ToString());

        File.WriteAllText(file, original.Replace("Unique = true", "Unique = false"));
        var changed = await workspace.RunJsonAsync("validate");
        changed.Process.ExitCode.ShouldBe(4, changed.Process.ToString());
        changed.Result.GetProperty("issues").EnumerateArray()
            .Select(i => (i.GetProperty("kind").GetString(), i.GetProperty("migrationId").GetString(), i.GetProperty("severity").GetString()))
            .ShouldBe([("checksumMismatch", "20260101000002", "error")]);

        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(4);
    }
}

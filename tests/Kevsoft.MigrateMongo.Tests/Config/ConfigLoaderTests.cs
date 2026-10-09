using Kevsoft.MigrateMongo.Config;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Config;

public sealed class ConfigLoaderTests
{
    private static readonly Dictionary<string, string> Variables = new()
    {
        ["MONGODB_URL"] = "mongodb://local:27017",
        ["PROD_MONGODB_URL"] = "mongodb://prod:27017",
    };

    private static string? Env(string name) => Variables.GetValueOrDefault(name);

    private const string Yaml = """
        project: src/MyApp.Migrations
        mongodb:
          url: ${MONGODB_URL}
          databaseName: myapp
        lockTtl: 120
        transaction: never
        environments:
          prod:
            mongodb:
              url: ${PROD_MONGODB_URL}
            lockWaitTimeout: 5
        """;

    [Fact]
    public void Loads_settings_and_interpolates_environment_variables()
    {
        var config = ConfigLoader.Load(Yaml, environment: null, Env);

        config.Project.ShouldBe("src/MyApp.Migrations");
        config.MongoUrl.ShouldBe("mongodb://local:27017");
        config.DatabaseName.ShouldBe("myapp");
        config.LockTtlSeconds.ShouldBe(120);
        config.LockWaitTimeoutSeconds.ShouldBe(60);
        config.Transaction.ShouldBe("never");
        config.OnChecksumMismatch.ShouldBe("fail");
        config.StateCollection.ShouldBe("_migrations");
        config.Environment.ShouldBeNull();
    }

    [Fact]
    public void Environment_overlay_is_merged_over_the_base_settings()
    {
        var config = ConfigLoader.Load(Yaml, "prod", Env);

        config.MongoUrl.ShouldBe("mongodb://prod:27017");
        config.DatabaseName.ShouldBe("myapp");
        config.LockTtlSeconds.ShouldBe(120);
        config.LockWaitTimeoutSeconds.ShouldBe(5);
        config.Environment.ShouldBe("prod");
    }

    [Fact]
    public void Variables_only_needed_by_the_base_settings_are_not_required_when_overridden()
    {
        var config = ConfigLoader.Load(Yaml, "prod", name => name == "PROD_MONGODB_URL" ? "mongodb://prod" : null);

        config.MongoUrl.ShouldBe("mongodb://prod");
    }

    [Fact]
    public void Unknown_environment_is_an_error()
    {
        Should.Throw<ConfigException>(() => ConfigLoader.Load(Yaml, "staging", Env)).Message.ShouldContain("staging");
    }

    [Fact]
    public void Missing_environment_variable_is_an_error()
    {
        Should.Throw<ConfigException>(() => ConfigLoader.Load(Yaml, null, _ => null)).Message.ShouldContain("MONGODB_URL");
    }

    [Fact]
    public void Project_only_loading_does_not_need_connection_variables()
    {
        var config = ConfigLoader.Load(Yaml, null, _ => null, projectOnly: true);

        config.Project.ShouldBe("src/MyApp.Migrations");
    }

    [Fact]
    public void Default_value_is_used_when_the_variable_is_not_set()
    {
        var config = ConfigLoader.Load("mongodb:\n  url: ${NOPE:-mongodb://fallback}\n  databaseName: db\n", null, _ => null);

        config.MongoUrl.ShouldBe("mongodb://fallback");
    }

    [Theory]
    [InlineData("unknownSetting: 1", "unknownSetting")]
    [InlineData("mongodb:\n  uri: x", "mongodb.uri")]
    [InlineData("transaction: sometimes", "transaction")]
    [InlineData("lockTtl: soon", "lockTtl")]
    public void Invalid_settings_are_reported(string yaml, string expected)
    {
        Should.Throw<ConfigException>(() => ConfigLoader.Load(yaml, null, Env)).Message.ShouldContain(expected);
    }

    [Fact]
    public void Tunnel_is_reported_as_not_yet_supported()
    {
        Should.Throw<ConfigException>(() => ConfigLoader.Load("tunnel:\n  host: bastion", null, Env)).Message.ShouldContain("tunnel");
    }

    [Fact]
    public void Ensure_connectable_requires_url_and_database()
    {
        Should.Throw<ConfigException>(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { DatabaseName = "db" }));
        Should.Throw<ConfigException>(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { MongoUrl = "mongodb://x" }));
        Should.NotThrow(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { MongoUrl = "mongodb://x", DatabaseName = "db" }));
    }

    [Fact]
    public void Project_is_resolved_relative_to_the_config_file()
    {
        var directory = Directory.CreateTempSubdirectory("mm-config-");
        try
        {
            var path = Path.Combine(directory.FullName, "migrate-mongo.yaml");
            File.WriteAllText(path, "project: src/Migrations\n");

            ConfigLoader.LoadFile(path, null, Env).Project.ShouldBe(Path.Combine(directory.FullName, "src", "Migrations"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

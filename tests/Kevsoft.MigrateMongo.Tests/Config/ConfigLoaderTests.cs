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
    [InlineData("results:\n  maxSize: 10", "results.maxSize")]
    [InlineData("results: 5", "results")]
    public void Invalid_settings_are_reported(string yaml, string expected)
    {
        Should.Throw<ConfigException>(() => ConfigLoader.Load(yaml, null, Env)).Message.ShouldContain(expected);
    }

    [Fact]
    public void Transaction_retry_timeout_defaults_to_120_seconds_and_can_be_overridden_per_environment()
    {
        ConfigLoader.Load(Yaml, null, Env).TransactionRetryTimeoutSeconds.ShouldBe(120);

        var config = ConfigLoader.Load("transactionRetryTimeout: 30\nenvironments:\n  prod:\n    transactionRetryTimeout: 0\n", "prod", Env);

        config.TransactionRetryTimeoutSeconds.ShouldBe(0);
        ConfigLoader.Load("transactionRetryTimeout: 30\n", null, Env).TransactionRetryTimeoutSeconds.ShouldBe(30);
    }

    [Fact]
    public void Tunnel_settings_are_loaded_and_interpolated()
    {
        var config = ConfigLoader.Load("""
            tunnel:
              host: bastion.example.com
              port: 2222
              user: deploy
              privateKey: ${SSH_KEY}
              passphrase: secret
              hostKeyFingerprint: SHA256:abc
              mode: local
            """, null, name => name == "SSH_KEY" ? "-----BEGIN KEY-----" : null);

        var tunnel = config.Tunnel.ShouldNotBeNull();
        tunnel.Host.ShouldBe("bastion.example.com");
        tunnel.Port.ShouldBe(2222);
        tunnel.User.ShouldBe("deploy");
        tunnel.PrivateKey.ShouldBe("-----BEGIN KEY-----");
        tunnel.PrivateKeyPath.ShouldBeNull();
        tunnel.Passphrase.ShouldBe("secret");
        tunnel.HostKeyFingerprint.ShouldBe("SHA256:abc");
        tunnel.Mode.ShouldBe("local");
    }

    [Fact]
    public void Tunnel_defaults_to_port_22_and_socks_mode()
    {
        var tunnel = ConfigLoader.Load("tunnel:\n  host: bastion\n  user: deploy\n  password: pw\n", null, Env).Tunnel.ShouldNotBeNull();

        tunnel.Port.ShouldBe(22);
        tunnel.Mode.ShouldBe("socks");
    }

    [Fact]
    public void No_tunnel_section_means_no_tunnel()
    {
        ConfigLoader.Load(Yaml, null, Env).Tunnel.ShouldBeNull();
    }

    [Fact]
    public void Tunnel_can_be_added_by_an_environment_overlay()
    {
        const string yaml = """
            mongodb:
              url: mongodb://db:27017
              databaseName: app
            environments:
              prod:
                tunnel:
                  host: bastion
                  user: deploy
                  password: pw
            """;

        ConfigLoader.Load(yaml, null, Env).Tunnel.ShouldBeNull();
        ConfigLoader.Load(yaml, "prod", Env).Tunnel.ShouldNotBeNull().Host.ShouldBe("bastion");
    }

    [Theory]
    [InlineData("tunnel:\n  user: u\n  password: p", "tunnel.host")]
    [InlineData("tunnel:\n  host: h\n  password: p", "tunnel.user")]
    [InlineData("tunnel:\n  host: h\n  user: u", "password")]
    [InlineData("tunnel:\n  host: h\n  user: u\n  password: p\n  port: 70000", "tunnel.port")]
    [InlineData("tunnel:\n  host: h\n  user: u\n  password: p\n  mode: magic", "mode")]
    [InlineData("tunnel:\n  host: h\n  user: u\n  privateKey: k\n  privateKeyPath: f", "both")]
    [InlineData("tunnel:\n  host: h\n  user: u\n  password: p\n  hostKeyFingerprint: MD5:aa", "SHA256")]
    [InlineData("tunnel:\n  host: h\n  user: u\n  password: p\n  hostname: x", "tunnel.hostname")]
    [InlineData("tunnel: bastion", "tunnel")]
    public void Invalid_tunnel_settings_are_reported(string yaml, string expected)
    {
        Should.Throw<ConfigException>(() => ConfigLoader.Load(yaml, null, Env)).Message.ShouldContain(expected);
    }

    [Fact]
    public void Tunnel_key_paths_are_resolved_relative_to_the_config_file()
    {
        var directory = Directory.CreateTempSubdirectory("mm-config-");
        try
        {
            var path = Path.Combine(directory.FullName, "migrate-mongo.yaml");
            File.WriteAllText(path, "tunnel:\n  host: h\n  user: u\n  privateKeyPath: keys/id_ed25519\n  knownHostsPath: ~/.ssh/known_hosts\n");

            var tunnel = ConfigLoader.LoadFile(path, null, Env).Tunnel.ShouldNotBeNull();

            tunnel.PrivateKeyPath.ShouldBe(Path.Combine(directory.FullName, "keys", "id_ed25519"));
            tunnel.KnownHostsPath.ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Results_capture_statements_up_to_64_KB_by_default_and_can_be_changed_per_environment()
    {
        var yaml = Yaml + """

            results:
              maxBytes: 1000
            """;
        yaml = yaml.Replace("lockWaitTimeout: 5", "lockWaitTimeout: 5\n    results:\n      captureStatements: false", StringComparison.Ordinal);

        var defaults = ConfigLoader.Load(Yaml, null, Env);
        defaults.ResultsCaptureStatements.ShouldBeTrue();
        defaults.ResultsMaxBytes.ShouldBe(65536);

        var local = ConfigLoader.Load(yaml, null, Env);
        local.ResultsCaptureStatements.ShouldBeTrue();
        local.ResultsMaxBytes.ShouldBe(1000);

        var prod = ConfigLoader.Load(yaml, "prod", Env);
        prod.ResultsCaptureStatements.ShouldBeFalse();
        prod.ResultsMaxBytes.ShouldBe(1000);
    }

    [Fact]
    public void Unknown_results_settings_in_an_environment_are_reported()
    {
        var yaml = Yaml.Replace("lockWaitTimeout: 5", "lockWaitTimeout: 5\n    results:\n      capture: false", StringComparison.Ordinal);

        Should.Throw<ConfigException>(() => ConfigLoader.Load(yaml, "prod", Env)).Message
            .ShouldBe("Unknown config setting 'environments.prod.results.capture'.");
    }

    [Fact]
    public void Results_max_bytes_must_be_positive()
    {
        Should.Throw<ConfigException>(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { MongoUrl = "mongodb://x", DatabaseName = "db", ResultsMaxBytes = 0 }))
            .Message.ShouldBe("'results.maxBytes' must be positive.");
    }

    [Fact]
    public void Ensure_connectable_requires_url_and_database()
    {
        Should.Throw<ConfigException>(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { DatabaseName = "db" }));
        Should.Throw<ConfigException>(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { MongoUrl = "mongodb://x" }));
        Should.NotThrow(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { MongoUrl = "mongodb://x", DatabaseName = "db" }));
        Should.Throw<ConfigException>(() => ConfigLoader.EnsureConnectable(new ResolvedConfig { MongoUrl = "mongodb://x", DatabaseName = "db", TransactionRetryTimeoutSeconds = -1 }))
            .Message.ShouldContain("transactionRetryTimeout");
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

    [Fact]
    public void Migration_template_is_resolved_relative_to_the_config_file_even_when_only_loading_the_project()
    {
        var directory = Directory.CreateTempSubdirectory("mm-config-");
        try
        {
            var path = Path.Combine(directory.FullName, "migrate-mongo.yaml");
            File.WriteAllText(path, "project: src/Migrations\nmigrationTemplate: templates/migration.cs.template\n");

            ConfigLoader.LoadFile(path, null, Env, projectOnly: true).MigrationTemplate
                .ShouldBe(Path.Combine(directory.FullName, "templates", "migration.cs.template"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

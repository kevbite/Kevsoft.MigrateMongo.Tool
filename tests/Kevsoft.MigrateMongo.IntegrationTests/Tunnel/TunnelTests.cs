using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Tunnel;

/// <summary>
/// Runs migrations against a MongoDB that only the SSH bastion can reach (see <see cref="TunnelEnvironment"/>).
/// </summary>
public sealed class TunnelTests(IntegrationEnvironment environment, TunnelEnvironment tunnel) : IClassFixture<TunnelEnvironment>
{
    private static readonly string[] AppliedIds = ["20260101000001", "20260101000002", "20260101000003"];

    private Workspace CreateWorkspace(string tunnelYaml, string? url = null)
    {
        var workspace = Workspace.FromFixture(environment, "BasicMigrations", client: tunnel.Client);
        workspace.WriteConfigFile($"""
            project: BasicMigrations
            mongodb:
              url: {url ?? tunnel.PrivateMongoUrl}
              databaseName: {workspace.DatabaseName}
            lockWaitTimeout: 5
            tunnel:
              host: {tunnel.BastionHost}
              port: {tunnel.BastionPort}
              user: {TunnelEnvironment.User}
            {Indent(tunnelYaml)}
            """);
        return workspace;
    }

    private static string Indent(string yaml) => string.Join("\n", yaml.Split('\n').Select(l => "  " + l.TrimEnd('\r')));

    private List<string> AppliedMigrations(Workspace workspace) =>
        workspace.Find("_migrations").Where(s => s["state"].AsString == "applied").Select(s => s["_id"].AsString).ToList();

    [Fact]
    public async Task Socks_tunnel_with_a_key_and_pinned_fingerprint_applies_and_rolls_back_migrations()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), "migrate-mongo-it-key-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(keyPath, tunnel.PrivateKey, TestContext.Current.CancellationToken);
        try
        {
            // No directConnection: the driver discovers the replica set member (mongo:27017) through the proxy too.
            await using var workspace = CreateWorkspace($"""
                privateKeyPath: {keyPath}
                hostKeyFingerprint: {tunnel.HostKeyFingerprint}
                """);

            var up = await workspace.RunAsync("up");

            up.ExitCode.ShouldBe(0, up.ToString());
            up.Output.ShouldContain($"Tunnel: tunnel@{tunnel.BastionHost}:{tunnel.BastionPort}, SOCKS proxy 127.0.0.1:");
            AppliedMigrations(workspace).ShouldBe(AppliedIds);
            workspace.Find("users").ShouldNotBeEmpty();

            var down = await workspace.RunAsync("down", "--no-build");

            down.ExitCode.ShouldBe(0, down.ToString());
            AppliedMigrations(workspace).ShouldBe(AppliedIds[..2]);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task Local_tunnel_with_a_password_and_known_hosts_applies_migrations()
    {
        await using var workspace = CreateWorkspace("""
            password: ${TUNNEL_PASSWORD}
            knownHostsPath: known_hosts
            mode: local
            """);
        await File.WriteAllTextAsync(workspace.PathOf("known_hosts"),
            $"[{tunnel.BastionHost}]:{tunnel.BastionPort} {tunnel.HostPublicKey}\n", TestContext.Current.CancellationToken);

        var up = await workspace.RunAsync(["up"], new Dictionary<string, string?> { ["TUNNEL_PASSWORD"] = TunnelEnvironment.Password });

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("127.0.0.1:");
        up.Output.ShouldContain("→ mongo:27017");
        AppliedMigrations(workspace).ShouldBe(AppliedIds);

        var down = await workspace.RunAsync(["down", "--batch", "--no-build"], new Dictionary<string, string?> { ["TUNNEL_PASSWORD"] = TunnelEnvironment.Password });

        down.ExitCode.ShouldBe(0, down.ToString());
        AppliedMigrations(workspace).ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_host_key_fingerprint_refuses_to_connect()
    {
        const string wrong = "SHA256:XYPAftl5eLwQJty/XfF9YzBlVihmnX2HIO351jJewMk";
        await using var workspace = CreateWorkspace($"""
            password: {TunnelEnvironment.Password}
            hostKeyFingerprint: {wrong}
            """);

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(1, up.ToString());
        up.Error.ShouldContain($"the server's host key fingerprint is {tunnel.HostKeyFingerprint}, not the configured 'hostKeyFingerprint' {wrong}");
        workspace.CollectionNames().ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_host_fails_unless_host_key_checking_is_explicitly_skipped()
    {
        await using var workspace = CreateWorkspace($"""
            password: {TunnelEnvironment.Password}
            knownHostsPath: empty_known_hosts
            """);
        await File.WriteAllTextAsync(workspace.PathOf("empty_known_hosts"), "", TestContext.Current.CancellationToken);

        var refused = await workspace.RunAsync("up");

        refused.ExitCode.ShouldBe(1, refused.ToString());
        refused.Error.ShouldContain($"Set 'tunnel.hostKeyFingerprint: {tunnel.HostKeyFingerprint}'");
        workspace.CollectionNames().ShouldBeEmpty();

        var insecure = await workspace.RunAsync("up", "--no-build", "--insecure-skip-host-key-check");

        insecure.ExitCode.ShouldBe(0, insecure.ToString());
        insecure.Output.ShouldContain("host key was not verified");
        AppliedMigrations(workspace).ShouldBe(AppliedIds);
    }

    [Fact]
    public async Task Without_the_tunnel_the_server_is_unreachable()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations", client: tunnel.Client);
        workspace.WriteConfigFile($"""
            project: BasicMigrations
            mongodb:
              url: {tunnel.PrivateMongoUrl}?serverSelectionTimeoutMS=3000
              databaseName: {workspace.DatabaseName}
            """);

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldNotBe(0, up.ToString());
        workspace.CollectionNames().ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_password_reports_an_authentication_failure()
    {
        await using var workspace = CreateWorkspace($"""
            password: not-the-password
            hostKeyFingerprint: {tunnel.HostKeyFingerprint}
            """);

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(1, up.ToString());
        up.Error.ShouldContain("authentication as 'tunnel' failed");
    }
}

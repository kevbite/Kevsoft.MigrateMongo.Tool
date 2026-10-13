using System.Net;
using System.Net.Sockets;
using System.Text;
using Kevsoft.MigrateMongo.Config;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Kevsoft.MigrateMongo.Tunnel;

/// <summary>Opens SSH tunnels with SSH.NET: <c>TunnelProvider = new SshTunnelProvider()</c>.</summary>
public sealed class SshTunnelProvider : IMongoTunnelProvider
{
    public async Task<IMongoTunnel> OpenAsync(TunnelConfig config, string mongoUrl, MongoTunnelOptions options, CancellationToken cancellationToken) =>
        await SshTunnel.OpenAsync(config, mongoUrl, options.DriverVersion, options.SkipHostKeyCheck, options.Warn ?? (_ => { }), cancellationToken);
}

/// <summary>An SSH connection to a bastion host with a forwarded port the MongoDB driver connects through.</summary>
internal sealed class SshTunnel : IMongoTunnel
{
    /// <summary>The first MongoDB.Driver version that supports SOCKS5 proxies.</summary>
    public static readonly Version MinimumSocksDriver = new(3, 12, 0);

    private readonly SshClient _client;
    private readonly ForwardedPort _port;

    private SshTunnel(SshClient client, ForwardedPort port, string mongoUrl, string description)
    {
        _client = client;
        _port = port;
        MongoUrl = mongoUrl;
        Description = description;
    }

    /// <summary>The connection string the runner should use.</summary>
    public string MongoUrl { get; }

    public string Description { get; }

    public static async Task<SshTunnel> OpenAsync(
        TunnelConfig config,
        string mongoUrl,
        string? driverVersion,
        bool skipHostKeyCheck,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        var socks = config.Mode == TunnelConfig.SocksMode;
        if (socks && driverVersion is not null && ParseVersion(driverVersion) is { } version && version < MinimumSocksDriver)
        {
            throw new ConfigException(
                $"'tunnel' mode 'socks' needs MongoDB.Driver {MinimumSocksDriver} or later in the migrations project (it has {driverVersion}). " +
                "Update MongoDB.Driver, or use 'mode: local' to forward a single server.");
        }

        var localPort = FreeLocalPort();
        string url;
        ForwardedPort port;
        string description;
        if (socks)
        {
            url = MongoUrlRewriter.ForSocks(mongoUrl, localPort);
            port = new ForwardedPortDynamic(IPAddress.Loopback.ToString(), (uint)localPort);
            description = $"SOCKS proxy 127.0.0.1:{localPort}";
        }
        else
        {
            (url, var host, var hostPort) = MongoUrlRewriter.ForLocal(mongoUrl, localPort);
            port = new ForwardedPortLocal(IPAddress.Loopback.ToString(), (uint)localPort, host, (uint)hostPort);
            description = $"127.0.0.1:{localPort} → {host}:{hostPort}";
            if (MongoUrlRewriter.UsesTls(mongoUrl))
            {
                warn($"TLS certificates won't match 127.0.0.1 through a 'local' tunnel; use 'mode: socks' or add tlsAllowInvalidHostnames=true if you accept that.");
            }
        }

        var client = new SshClient(CreateConnectionInfo(config));
        string? hostKeyProblem = null;
        client.HostKeyReceived += (_, e) =>
        {
            hostKeyProblem = skipHostKeyCheck ? null : CheckHostKey(config, e.HostKey);
            e.CanTrust = hostKeyProblem is null;
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
            client.AddForwardedPort(port);
            port.Exception += (_, e) => warn($"SSH tunnel: {e.Exception.Message}");
            port.Start();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            client.Dispose();
            port.Dispose();
            var reason = hostKeyProblem ?? e switch
            {
                SshAuthenticationException => $"authentication as '{config.User}' failed ({e.Message})",
                _ => e.Message,
            };
            throw new MongoTunnelException($"Couldn't open the SSH tunnel to {config.Host}:{config.Port}: {reason}", e);
        }

        return new SshTunnel(client, port, url, $"{config.User}@{config.Host}:{config.Port}, {description}");
    }

    public void Dispose()
    {
        try
        {
            _port.Stop();
        }
        catch (Exception)
        {
            // Closing anyway.
        }

        _port.Dispose();
        _client.Dispose();
    }

    /// <summary>Returns why the host key isn't trusted, or <c>null</c> if it is.</summary>
    private static string? CheckHostKey(TunnelConfig config, byte[] hostKey)
    {
        var fingerprint = HostKeyVerifier.Fingerprint(hostKey);
        if (config.HostKeyFingerprint is { } expected)
        {
            return HostKeyVerifier.MatchesFingerprint(hostKey, expected)
                ? null
                : $"the server's host key fingerprint is {fingerprint}, not the configured 'hostKeyFingerprint' {expected}. If the server's key changed on purpose, update the config.";
        }

        var knownHosts = config.KnownHostsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        if (File.Exists(knownHosts) && HostKeyVerifier.IsKnown(File.ReadLines(knownHosts), config.Host, config.Port, hostKey))
        {
            return null;
        }

        return $"the server's host key ({fingerprint}) isn't trusted: it isn't in {knownHosts}. " +
               $"Set 'tunnel.hostKeyFingerprint: {fingerprint}' after checking it with the server's administrator, " +
               "or add the host to known_hosts (ssh-keyscan).";
    }

    private static ConnectionInfo CreateConnectionInfo(TunnelConfig config)
    {
        var methods = new List<AuthenticationMethod>();
        if (config.PrivateKey is not null || config.PrivateKeyPath is not null)
        {
            PrivateKeyFile key;
            try
            {
                key = config.PrivateKey is not null
                    ? new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(NormaliseKey(config.PrivateKey))), config.Passphrase)
                    : new PrivateKeyFile(config.PrivateKeyPath!, config.Passphrase);
            }
            catch (Exception e) when (e is SshException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new MongoTunnelException($"Couldn't read the SSH private key{(config.PrivateKeyPath is null ? "" : $" '{config.PrivateKeyPath}'")}: {e.Message}", e);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(config.User, key));
        }

        if (config.Password is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(config.User, password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(config.User);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(interactive);
        }

        return new ConnectionInfo(config.Host, config.Port, config.User, [.. methods]) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>CI secrets often lose their line breaks or have literal <c>\n</c>s; put them back.</summary>
    private static string NormaliseKey(string key) => key.Replace("\\n", "\n").Replace("\r\n", "\n").Trim() + "\n";

    private static Version? ParseVersion(string version) =>
        Version.TryParse(version.Split('-', '+')[0], out var parsed) ? parsed : null;

    private static int FreeLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

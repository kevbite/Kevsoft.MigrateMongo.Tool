namespace Kevsoft.MigrateMongo.Config;

/// <summary>
/// Opens the <c>tunnel</c> from the config file. The migrate-mongo tool always has one; the standalone runner, bundles
/// and in-app hosting get one by referencing <c>Kevsoft.MigrateMongo.Tunnel</c> and setting <c>new SshTunnelProvider()</c>.
/// </summary>
public interface IMongoTunnelProvider
{
    /// <exception cref="MongoTunnelException">The tunnel couldn't be opened.</exception>
    /// <exception cref="ConfigException">The tunnel can't be used with this connection string.</exception>
    Task<IMongoTunnel> OpenAsync(TunnelConfig config, string mongoUrl, MongoTunnelOptions options, CancellationToken cancellationToken);
}

/// <summary>An open tunnel. Dispose it to close the tunnel.</summary>
public interface IMongoTunnel : IDisposable
{
    /// <summary>The connection string to use instead of the configured one.</summary>
    string MongoUrl { get; }

    /// <summary>For logging, e.g. <c>user@bastion:22, SOCKS proxy 127.0.0.1:50123</c>.</summary>
    string Description { get; }
}

public sealed class MongoTunnelOptions
{
    /// <summary>The MongoDB.Driver version that will connect, to check the tunnel mode is supported.</summary>
    public string? DriverVersion { get; init; }

    /// <summary>Don't verify the SSH server's host key (not recommended).</summary>
    public bool SkipHostKeyCheck { get; init; }

    /// <summary>Called with warnings, e.g. about TLS through a local forward.</summary>
    public Action<string>? Warn { get; init; }
}

public sealed class MongoTunnelException(string message, Exception? innerException = null) : Exception(message, innerException);

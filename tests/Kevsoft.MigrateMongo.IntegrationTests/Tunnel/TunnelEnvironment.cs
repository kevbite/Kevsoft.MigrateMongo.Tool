using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.IntegrationTests.Tunnel;

/// <summary>
/// A private Docker network with MongoDB (reachable only as <c>mongo:27017</c> inside the network) and an SSH bastion
/// whose port 22 is published. The tool can only reach <c>mongo</c> through the bastion, so a working run proves the
/// tunnel. The test itself checks the results through MongoDB's published port.
/// </summary>
public sealed class TunnelEnvironment : IAsyncLifetime
{
    private const string AtlasLocalImage = "mongodb/mongodb-atlas-local:8.0";

    private INetwork _network = null!;
    private IFutureDockerImage _bastionImage = null!;
    private IContainer _mongo = null!;
    private IContainer _bastion = null!;

    /// <summary>The connection string as seen from inside the network (only resolvable by the bastion).</summary>
    public string PrivateMongoUrl => "mongodb://mongo:27017/";

    /// <summary>For assertions from the test process.</summary>
    public IMongoClient Client { get; private set; } = null!;

    public string BastionHost => _bastion.Hostname;

    public int BastionPort => _bastion.GetMappedPublicPort(22);

    public const string User = "tunnel";

    public const string Password = "tunnel-password";

    /// <summary>The bastion's host key fingerprint, from <c>ssh-keygen -lf</c> inside the container.</summary>
    public string HostKeyFingerprint { get; private set; } = "";

    /// <summary>The bastion's host public key line (<c>ssh-ed25519 AAAA...</c>) for known_hosts.</summary>
    public string HostPublicKey { get; private set; } = "";

    /// <summary>The OpenSSH private key authorised for <see cref="User"/>.</summary>
    public string PrivateKey { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder().WithName("migrate-mongo-it-" + Guid.NewGuid().ToString("N")[..8]).Build();
        await _network.CreateAsync();

        _bastionImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "Tunnel"))
            .WithName("migrate-mongo-it-bastion:latest")
            .WithCleanUp(true)
            .Build();

        _mongo = new ContainerBuilder(AtlasLocalImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mongo")
            .WithHostname("mongo")
            .WithPortBinding(27017, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();

        await Task.WhenAll(_bastionImage.CreateAsync(), _mongo.StartAsync());

        _bastion = new ContainerBuilder(_bastionImage)
            .WithNetwork(_network)
            .WithPortBinding(22, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening"))
            .Build();
        await _bastion.StartAsync();

        Client = new MongoClient($"mongodb://{_mongo.Hostname}:{_mongo.GetMappedPublicPort(27017)}/?directConnection=true");

        var fingerprint = await _bastion.ExecAsync(["ssh-keygen", "-lf", "/etc/ssh/ssh_host_ed25519_key.pub"]);
        HostKeyFingerprint = fingerprint.Stdout.Split(' ')[1];
        var publicKey = Encoding.UTF8.GetString(await _bastion.ReadFileAsync("/etc/ssh/ssh_host_ed25519_key.pub")).Split(' ');
        HostPublicKey = $"{publicKey[0]} {publicKey[1]}";
        PrivateKey = Encoding.UTF8.GetString(await _bastion.ReadFileAsync("/keys/id_ed25519"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in new[] { _bastion, _mongo })
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }

        if (_bastionImage is not null)
        {
            await _bastionImage.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }
}

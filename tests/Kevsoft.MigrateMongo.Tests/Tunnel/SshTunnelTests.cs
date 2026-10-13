using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Tunnel;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tunnel;

public sealed class SshTunnelTests
{
    [Theory]
    [InlineData("3.5.0")]
    [InlineData("3.11.2")]
    public async Task Socks_mode_needs_driver_3_12_or_later(string driver)
    {
        var config = new TunnelConfig { Host = "bastion.invalid", User = "u", Password = "p", Mode = "socks" };

        var error = await Should.ThrowAsync<ConfigException>(() =>
            SshTunnel.OpenAsync(config, "mongodb://db", driver, false, _ => { }, CancellationToken.None));

        error.Message.ShouldContain($"MongoDB.Driver 3.12.0 or later in the migrations project (it has {driver})");
        error.Message.ShouldContain("mode: local");
    }
}

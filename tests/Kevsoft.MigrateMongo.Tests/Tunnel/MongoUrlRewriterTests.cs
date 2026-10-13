using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Tunnel;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tunnel;

public sealed class MongoUrlRewriterTests
{
    [Theory]
    [InlineData("mongodb://db:27017", "mongodb://db:27017/?proxyHost=127.0.0.1&proxyPort=1080")]
    [InlineData("mongodb://db:27017/", "mongodb://db:27017/?proxyHost=127.0.0.1&proxyPort=1080")]
    [InlineData("mongodb://u:p%40ss@a:1,b:2/app?replicaSet=rs0&tls=true", "mongodb://u:p%40ss@a:1,b:2/app?replicaSet=rs0&tls=true&proxyHost=127.0.0.1&proxyPort=1080")]
    [InlineData("mongodb+srv://u:p@cluster0.abc.mongodb.net/?retryWrites=true", "mongodb+srv://u:p@cluster0.abc.mongodb.net/?retryWrites=true&proxyHost=127.0.0.1&proxyPort=1080")]
    [InlineData("mongodb://db?directConnection=true", "mongodb://db/?directConnection=true&proxyHost=127.0.0.1&proxyPort=1080")]
    public void Socks_adds_the_proxy_options(string url, string expected)
    {
        MongoUrlRewriter.ForSocks(url, 1080).ShouldBe(expected);
    }

    [Fact]
    public void Socks_refuses_a_url_that_already_has_a_proxy()
    {
        Should.Throw<ConfigException>(() => MongoUrlRewriter.ForSocks("mongodb://db/?ProxyHost=x", 1080)).Message.ShouldContain("proxyHost");
    }

    [Theory]
    [InlineData("mongodb://db", "mongodb://127.0.0.1:5000/?directConnection=true", "db", 27017)]
    [InlineData("mongodb://u:p@a.internal:27018,b.internal:27019/app?replicaSet=rs0", "mongodb://u:p@127.0.0.1:5000/app?replicaSet=rs0&directConnection=true", "a.internal", 27018)]
    [InlineData("mongodb://db:1/?directConnection=false&w=majority", "mongodb://127.0.0.1:5000/?w=majority&directConnection=true", "db", 1)]
    [InlineData("mongodb://[fd00::1]:27020/app", "mongodb://127.0.0.1:5000/app?directConnection=true", "fd00::1", 27020)]
    [InlineData("mongodb://[fd00::1]/app", "mongodb://127.0.0.1:5000/app?directConnection=true", "fd00::1", 27017)]
    public void Local_points_at_the_forwarded_port_and_returns_the_first_host(string url, string expectedUrl, string expectedHost, int expectedPort)
    {
        var (rewritten, host, port) = MongoUrlRewriter.ForLocal(url, 5000);

        rewritten.ShouldBe(expectedUrl);
        host.ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Fact]
    public void Local_refuses_srv_urls()
    {
        Should.Throw<ConfigException>(() => MongoUrlRewriter.ForLocal("mongodb+srv://cluster0.abc.mongodb.net", 5000)).Message.ShouldContain("socks");
    }

    [Theory]
    [InlineData("postgres://db")]
    [InlineData("mongodb:///app")]
    [InlineData("mongodb://db:notaport")]
    public void Invalid_urls_are_reported(string url)
    {
        Should.Throw<ConfigException>(() => MongoUrlRewriter.ForLocal(url, 5000));
    }

    [Theory]
    [InlineData("mongodb://db", false)]
    [InlineData("mongodb://db/?tls=true", true)]
    [InlineData("mongodb://db/?ssl=TRUE", true)]
    [InlineData("mongodb+srv://cluster0.abc.mongodb.net", true)]
    public void Detects_tls(string url, bool tls)
    {
        MongoUrlRewriter.UsesTls(url).ShouldBe(tls);
    }
}

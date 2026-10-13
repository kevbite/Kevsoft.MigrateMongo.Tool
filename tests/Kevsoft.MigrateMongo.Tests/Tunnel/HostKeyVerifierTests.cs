using Kevsoft.MigrateMongo.Tunnel;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Tunnel;

/// <summary>Expected values were produced by OpenSSH's ssh-keygen (-lf for the fingerprint, -H for hashed hosts).</summary>
public sealed class HostKeyVerifierTests
{
    private const string PublicKey = "AAAAC3NzaC1lZDI1NTE5AAAAIBpupvYTHjVWVJZK7a/9+iLYQK82oy+Mzo1i3hti0Oy2";
    private const string OtherKey = "AAAAC3NzaC1lZDI1NTE5AAAAIJ4c9lZlTeRuv2K0Mv1D+Hn2qQ2Zq4FvX0B6m3e9sYQx";
    private const string SshKeygenFingerprint = "SHA256:XYPAftl5eLwQJty/XfF9YzBlVihmnX2HIO351jJewMk";

    private static readonly byte[] Key = Convert.FromBase64String(PublicKey);

    [Fact]
    public void Fingerprint_matches_ssh_keygen()
    {
        HostKeyVerifier.Fingerprint(Key).ShouldBe(SshKeygenFingerprint);
    }

    [Theory]
    [InlineData(SshKeygenFingerprint, true)]
    [InlineData(" " + SshKeygenFingerprint + "= ", true)]
    [InlineData("SHA256:xYPAftl5eLwQJty/XfF9YzBlVihmnX2HIO351jJewMk", false)]
    [InlineData("SHA256:AAAAftl5eLwQJty/XfF9YzBlVihmnX2HIO351jJewMk", false)]
    public void Matches_fingerprint(string expected, bool matches)
    {
        HostKeyVerifier.MatchesFingerprint(Key, expected).ShouldBe(matches);
    }

    [Theory]
    [InlineData("bastion.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, true)]
    [InlineData("BASTION.example.com ssh-ed25519 " + PublicKey + " comment", "bastion.example.com", 22, true)]
    [InlineData("other,bastion.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, true)]
    [InlineData("bastion.example.com ssh-ed25519 " + OtherKey, "bastion.example.com", 22, false)]
    [InlineData("elsewhere.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, false)]
    [InlineData("bastion.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 2222, false)]
    [InlineData("[bastion.example.com]:2222 ssh-ed25519 " + PublicKey, "bastion.example.com", 2222, true)]
    [InlineData("*.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, true)]
    [InlineData("*.example.com,!bastion.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, false)]
    [InlineData("bastion.example.co? ssh-ed25519 " + PublicKey, "bastion.example.com", 22, true)]
    [InlineData("@cert-authority bastion.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, false)]
    [InlineData("# bastion.example.com ssh-ed25519 " + PublicKey, "bastion.example.com", 22, false)]
    [InlineData("|1|25FvToZ0+dmUyKLeEzBehx3As3E=|4uwqTV2OijDxitOqeSdvCPks3ig= ssh-ed25519 " + PublicKey, "bastion.example.com", 22, true)]
    [InlineData("|1|25FvToZ0+dmUyKLeEzBehx3As3E=|4uwqTV2OijDxitOqeSdvCPks3ig= ssh-ed25519 " + PublicKey, "other.example.com", 22, false)]
    [InlineData("|1|dhBo3tGMqKuCjIEAyKrTd2so7gI=|UlALkqJDDLz2Jzp3P9+/h59zybw= ssh-ed25519 " + PublicKey, "bastion.example.com", 2222, true)]
    [InlineData("|1|dhBo3tGMqKuCjIEAyKrTd2so7gI=|UlALkqJDDLz2Jzp3P9+/h59zybw= ssh-ed25519 " + PublicKey, "bastion.example.com", 22, false)]
    public void Known_hosts_entries(string line, string host, int port, bool known)
    {
        HostKeyVerifier.IsKnown([line], host, port, Key).ShouldBe(known);
    }

    [Fact]
    public void Revoked_key_is_never_trusted()
    {
        HostKeyVerifier.IsKnown([
            "bastion.example.com ssh-ed25519 " + PublicKey,
            "@revoked * ssh-ed25519 " + PublicKey,
        ], "bastion.example.com", 22, Key).ShouldBeFalse();
    }

    [Fact]
    public void Malformed_lines_are_skipped()
    {
        HostKeyVerifier.IsKnown([
            "",
            "garbage",
            "bastion.example.com ssh-ed25519 not-base64!",
            "|1|bad|hash ssh-ed25519 " + PublicKey,
            "bastion.example.com ssh-ed25519 " + PublicKey,
        ], "bastion.example.com", 22, Key).ShouldBeTrue();
    }
}

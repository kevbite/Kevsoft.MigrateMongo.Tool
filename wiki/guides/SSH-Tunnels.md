If the database is only reachable through an SSH jump host (bastion), add a `tunnel:` to the config, usually to an
environment. The tool opens the SSH connection itself (no `ssh` binary needed), verifies the host key, and gives the
driver a local SOCKS5 proxy, so every replica set member is reachable by the host names in the connection string.

## Example

```yaml
environments:
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}       # e.g. mongodb://db1.internal,db2.internal/?replicaSet=rs0
    tunnel:
      host: bastion.example.com
      user: deploy
      privateKey: ${BASTION_SSH_KEY}
      hostKeyFingerprint: SHA256:x9Yb...
```

## Settings

| Setting | Meaning |
|---|---|
| `host` / `port` | The SSH server. `host` is required; `port` defaults to `22`. |
| `user` | The SSH user. Required. |
| `password` | Password authentication (also answers keyboard-interactive prompts). |
| `privateKey` | The private key's contents, e.g. from a CI secret. Literal `\n` sequences become new lines. |
| `privateKeyPath` | A private key file. `~/` and paths relative to the config file work. |
| `passphrase` | The passphrase for an encrypted private key. |
| `hostKeyFingerprint` | The server's `SHA256:...` host key fingerprint, as printed by `ssh-keygen -lf`. |
| `knownHostsPath` | A `known_hosts` file to check the host key against. Defaults to `~/.ssh/known_hosts`. |
| `mode` | `socks` (default) or `local`. |

You need one of `password`, `privateKey` or `privateKeyPath` (not both key settings).

## Host key verification

The host key is always verified, against `hostKeyFingerprint` if set or the `known_hosts` file otherwise. If
verification fails, the error shows the server's fingerprint so you can pin it. To get it ahead of time:

```bash
ssh-keyscan bastion.example.com | ssh-keygen -lf -
```

`--insecure-skip-host-key-check` turns verification off. Avoid it outside throwaway environments.

## Modes

| `mode` | How it works | Needs |
|---|---|---|
| `socks` (default) | A local SOCKS5 proxy; the driver reaches every host in the connection string through it, so replica sets and `mongodb+srv://` work. | `MongoDB.Driver` 3.12 or later, the first version with SOCKS5 support. |
| `local` | Forwards a local port to the first host in the connection string and connects with `directConnection=true`. | Any driver version. |

`local` mode can't use `mongodb+srv://` URLs, and TLS host name validation fails unless the certificate also covers
`127.0.0.1`.

## Without the tool

When the tool drives the runner, the tool opens the tunnel. Running the project directly or as a [bundle](./Bundles.md)
rejects a `tunnel:` unless the project references `Kevsoft.MigrateMongo.Tunnel` and sets the provider:

```bash
dotnet add package Kevsoft.MigrateMongo.Tunnel
```

```csharp
using Kevsoft.MigrateMongo;
using Kevsoft.MigrateMongo.Tunnel;

return await MigrateMongoRunner.RunAsync(args, new MigrateMongoRunnerOptions
{
    TunnelProvider = new SshTunnelProvider(),
});
```

The package is opt-in, so SSH.NET is only in your dependencies if you need it. To tunnel from your app, see
[Running from Your Application](./Running-from-Your-Application.md#through-an-ssh-bastion).

## In CI

Store the key as a secret and pass it through an environment variable:

```yaml
    env:
      PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }}
      BASTION_SSH_KEY: ${{ secrets.BASTION_SSH_KEY }}
```

See [Environments and Secrets](./Environments-and-Secrets.md).

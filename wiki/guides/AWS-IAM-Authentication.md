Connect to MongoDB Atlas without a password, using an AWS IAM role (Atlas "AWS IAM" database users).

## 1. Add the package

Reference `Kevsoft.MigrateMongo.Aws` from the migrations project:

```bash
dotnet add package Kevsoft.MigrateMongo.Aws     # or: migrate-mongo init --auth aws
```

It pulls in `MongoDB.Driver.Authentication.AWS`, which must stay on the same version as your `MongoDB.Driver`
reference. `init --auth aws` pins both for you. If you add the package by hand, reference
`MongoDB.Driver.Authentication.AWS` at the same version as `MongoDB.Driver`.

## 2. Use `MONGODB-AWS` in the connection string

```yaml
environments:
  prod:
    mongodb:
      url: mongodb+srv://cluster0.xxxxx.mongodb.net/?authSource=%24external&authMechanism=MONGODB-AWS
      databaseName: myapp
```

The runner, bundles and Hosting register the mechanism with the driver when the URL asks for it, so no code is needed.
Without the package, a `MONGODB-AWS` URL fails before connecting with a message saying which package to add. If you
build your own `MongoClientSettings`, you can call `AwsAuthentication.Register()` yourself (it's idempotent).

## 3. Provide credentials

Credentials come from the standard AWS sources, so nothing secret goes in the URL:

- the `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and `AWS_SESSION_TOKEN` environment variables;
- web identity (`AWS_WEB_IDENTITY_TOKEN_FILE` and `AWS_ROLE_ARN`);
- the ECS task or EC2 instance role.

The tool passes its environment to the runner, so set these wherever you run `migrate-mongo`.

## 4. Create the Atlas user

In Atlas, create a database user with AWS IAM authentication for the **role's ARN**
(`arn:aws:iam::123456789012:role/deploy`), not the assumed-role session ARN.

Atlas still checks its IP access list against where the connection comes from: the CI runner, or the bastion if you
use a [`tunnel:`](./SSH-Tunnels.md) (`mode: socks` for `mongodb+srv://` URLs).

## GitHub Actions

Using OIDC to assume a role:

```yaml
    permissions: { id-token: write, contents: read }
    steps:
      - uses: aws-actions/configure-aws-credentials@v4
        with: { role-to-assume: arn:aws:iam::123456789012:role/deploy, aws-region: eu-west-2 }
      - run: dotnet tool run migrate-mongo up --env prod
```

## Bitbucket Pipelines

Using OIDC web identity (or any script that exports the `AWS_*` variables):

```yaml
- step:
    oidc: true
    script:
      - export AWS_ROLE_ARN=arn:aws:iam::123456789012:role/deploy
      - export AWS_WEB_IDENTITY_TOKEN_FILE=$(pwd)/web-identity-token
      - echo $BITBUCKET_STEP_OIDC_TOKEN > $(pwd)/web-identity-token
      - ./MyApp.Migrations up --env prod
```

## See also

- [Environments and Secrets](./Environments-and-Secrets.md)
- [Running in CI](./Running-in-CI.md)

Run migrations from your pipeline, gate pull requests and deployments on them, and build once to deploy everywhere.

## GitHub Actions

Install the tool as a [local tool](../getting-started/Installation.md) so CI uses the version in
`.config/dotnet-tools.json`:

```yaml
jobs:
  migrate:
    runs-on: ubuntu-latest
    environment: production
    # Every command that connects needs these, validate included.
    env:
      PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }}
      BASTION_SSH_KEY: ${{ secrets.BASTION_SSH_KEY }}   # if prod has a tunnel
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with: { global-json-file: global.json }
      - run: dotnet tool restore
      - run: dotnet tool run migrate-mongo validate --env prod
      - run: dotnet tool run migrate-mongo up --env prod
```

- `environment: production` scopes the secrets and can require an approval. See
  [Environments and Secrets](./Environments-and-Secrets.md).
- `validate` checks nothing was edited, removed or added out of order before anything runs.
- `up` exits non-zero on any failure, failing the job. See [Exit Codes](../reference/Exit-Codes.md).

## Pull request gate

```yaml
      - run: dotnet tool run migrate-mongo validate --env staging
```

[`validate`](../reference/commands/Command-validate.md) fails with exit code `4` if someone edited an applied
migration, removed one, or added one older than the newest applied one. A read-only database user is enough for it.

## Deployment gate

[`plan`](../reference/commands/Command-plan.md) exits with `2` when there's something to apply, so a pipeline can decide
whether a deployment step (or an approval) is needed:

```bash
migrate-mongo plan --env prod
case $? in
  0) echo "Nothing to deploy" ;;
  2) migrate-mongo up --env prod ;;
  *) exit 1 ;;
esac
```

`status --check` exits with `2` in the same cases, and suits a scheduled check for indexes changed by hand. Add
[`up --dry-run`](../reference/commands/Command-up.md#dry-runs) to see exactly what would run.

## Build once, deploy many

Build a [bundle](./Bundles.md) in the build job and run it on deploy agents that don't have .NET:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with: { global-json-file: global.json }
      - run: dotnet tool restore
      - run: dotnet tool run migrate-mongo bundle -r linux-x64 -o out
      - uses: actions/upload-artifact@v7
        with: { name: migrations, path: out/ }

  deploy:
    needs: build
    runs-on: [self-hosted, production]
    steps:
      - uses: actions/download-artifact@v8
        with: { name: migrations }
      - run: chmod +x MyApp.Migrations && ./MyApp.Migrations up --env prod
        env: { PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }} }
```

The same bundle runs against staging and production, so what you tested is what you deploy.

## Machine-readable output

`--json` writes one JSON object per line instead of text, ending with a `result` line carrying the exit code. Use it to
annotate a build or post a summary. See [Commands](../reference/Commands.md#json-output).

## Other CI systems

Nothing is GitHub-specific: install the .NET SDK, `dotnet tool restore`, set the connection variables, and run the
same commands. Or use the [Docker images](./Docker.md), or a bundle where there's no .NET. For a Bitbucket Pipelines
example, see [AWS IAM Authentication](./AWS-IAM-Authentication.md#bitbucket-pipelines).

## Many app instances

When many instances run migrations at once (e.g. [at startup](./Running-from-Your-Application.md)) or overlap with a
pipeline run, the lock ensures only one applies them; the others wait and then find nothing to do. See
[Safety](../concepts/Safety.md#locking).

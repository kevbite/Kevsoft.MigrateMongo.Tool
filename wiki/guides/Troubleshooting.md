What to do when a run fails. Every failure has an [exit code](../reference/Exit-Codes.md), and
[`status`](../reference/commands/Command-status.md) and [`history`](../reference/commands/Command-history.md) show the
state of each migration.

## A migration throws

Exit code `1`. The output shows the exception with its stack trace. With `--json`, it's the `details` of the
`migrationFailed` event, and Hosting logs the exception itself. The state and history keep the one-line error.

- **In a transaction**, nothing was saved and the migration is still pending: fix it and run `up` again.
- **Outside a transaction**, whatever it did before throwing is still there. Its state is `failed`, and later runs
  stop until you resolve it, as for a [crash](#a-migration-crashed-part-way-without-a-transaction): undo what it did (or
  make sure it's safe to repeat), fix it, run `mark <id> --unapplied`, then `up` again.

## An applied migration was edited

Exit code `4`:

```
✖ 20261014164027 initial was modified after being applied (applied sha256:8eaa3c91..., current sha256:c6700cfc...).
  Create a new migration instead, or run `mark 20261014164027 --applied` to accept the change.
```

Either revert the edit and write a new migration, or accept it with
[`mark <id> --applied`](../reference/commands/Command-mark.md), which stores the new checksum.
[`onChecksumMismatch: warn`](../reference/Configuration.md#migrations) turns this into a warning. Whitespace and
comments don't count as edits. See [Safety](../concepts/Safety.md#checksums).

## A migration crashed part-way without a transaction

Exit code `1`, and every later run stops:

```
✖ 20261014164029 split-full-name is in state 'applying' from run 6704b0c1 on build-agent-12. Inspect the data,
  then run `mark 20261014164029 --applied` or `mark 20261014164029 --unapplied`.
```

The process died (killed, out of memory, machine lost) while the migration was running, so nobody knows how far it got.
Look at the data, then:

- `mark <id> --applied` if it finished;
- `mark <id> --unapplied` if it should run again, after undoing what it did if it isn't safe to repeat.

The same applies to `rollingBack` after a `down`.

## Another run holds the lock

Exit code `3`:

```
… Waiting for lock held by run 6704b0c1 (build-agent-07), which expires at 2026-10-14 16:51:12Z.
✖ Timed out after 60s waiting for the migration lock held by run 6704b0c1 (build-agent-07), which expires at 2026-10-14 16:51:12Z.
```

Another run is in progress: wait for it, or raise [`lockWaitTimeout`](../reference/Configuration.md#migrations). Locks are
renewed while a run is in progress and expire `lockTtl` seconds after a run dies, so a crashed run's lock clears on its
own.

To remove one sooner, use [`unlock --force`](../reference/commands/Command-unlock.md), but only if you're sure its run has
stopped.

## A collection change is blocked

Exit code `4` from `plan` or `up`, with `✖ blocked` next to the change and an error saying why. Nothing was changed.
Each has its own way through:

| Blocked by | See |
|---|---|
| `indexes.onRebuild: fail` | [Rebuilds](./Indexes.md#rebuilds) (`--allow-rebuild`) |
| No declared index uses a field the existing ones use | [Typed definitions](./Managing-Collections.md#typed-definitions) (`--allow-unmatched`) |
| Converting to capped | [Converting to capped](./Collection-Options.md#converting-to-capped) (`--allow-convert-to-capped`) |
| An option MongoDB can't change | [Changes MongoDB can't make](./Collection-Options.md#changes-mongodb-cant-make) (`--skip-unchangeable`) |
| Existing documents fail a validator | [Validators](./Validators.md#existing-documents) (`--allow-invalid-documents`) |
| A conflict with an ignored index | [Undeclared indexes](./Indexes.md#undeclared-indexes) |
| A collection with a view's name | [Views](./Views.md#what-up-does) |

## A definition is skipped as superseded

`status` shows `superseded`, and `up` warns. A newer build replaced this definition recently, so an older build won't
undo it. See [rolling deploys](./Managing-Collections.md#rolling-deploys). Use `--allow-superseded` if you're rolling back
on purpose.

## The build fails

Exit code `6`. The tool only shows build output when a build fails. `MM` codes come from the source generator; see
[Build Errors](../reference/Build-Errors.md).

## The tool is older than the runner

Exit code `5`. Update the tool:

```
dotnet tool update Kevsoft.MigrateMongo.Tool
```

See [How It Works](../concepts/How-It-Works.md).

## Stopping a run

Ctrl+C and `SIGTERM` cancel the running migration through its `CancellationToken`, so pass it to every driver call.
The lock is kept until the migration's code returns, even if it ignores the token, and is then released. A transactional
migration is rolled back. One outside a transaction that ran to the end is recorded as applied, so a cancel doesn't
leave a finished migration marked `failed`.

## Can't connect

- Check the variables the config references are set: an unset one is reported before connecting.
- For a bastion, see [SSH Tunnels](./SSH-Tunnels.md). A failed host key check prints the server's fingerprint.
- For `MONGODB-AWS`, see [AWS IAM Authentication](./AWS-IAM-Authentication.md).

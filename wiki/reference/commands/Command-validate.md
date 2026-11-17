Checks the applied migrations against the code. Read-only, so a read-only database user is enough.

```
migrate-mongo validate
```

```
$ migrate-mongo validate
Validation passed.
```

It fails with exit code `4` if:

- an applied migration was edited (its [checksum](../../concepts/Safety.md#checksums) changed), unless
  `onChecksumMismatch` is `warn` or `ignore`;
- an applied migration is missing from the code;
- a pending migration is older than the newest applied one, unless `allowOutOfOrder` is set.

Run it in pull request builds against a shared environment, so an edited migration is caught before it's merged. See
[Running in CI](../../guides/Running-in-CI.md).

## Exit codes

`0` when everything matches, `4` when it doesn't. See [Exit Codes](../Exit-Codes.md).

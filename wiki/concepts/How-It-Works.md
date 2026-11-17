How `migrate-mongo`, your migrations project and your app fit together, and why upgrading one doesn't break the others.

## The pieces

```
 ┌──────────────────────────────┐   build (your SDK)      ┌─────────────────────────────────────────┐
 │ migrate-mongo (the tool)     │ ──────────────────────▶ │ Your migrations project (console app)    │
 │ Native AOT, no .NET needed   │                         │  ├─ Kevsoft.MigrateMongo.Runner          │
 │  • config, --env, secrets    │  spawn + handshake      │  ├─ Kevsoft.MigrateMongo (engine)        │
 │  • init / create             │ ──────────────────────▶ │  ├─ MongoDB.Driver (your version)        │
 │  • build, bundles            │  JSON events (stdout)   │  └─ Migrations/*.cs, Collections/*.cs    │
 │  • SSH tunnels               │ ◀────────────────────── │                                          │
 │  • output, exit codes        │                         └─────────────────────────────────────────┘
 └──────────────────────────────┘                                ▲ same engine, state and lock
                                                          ┌──────┴──────────────────────────────────┐
                                                          │ Your app: Kevsoft.MigrateMongo.Hosting   │
                                                          └─────────────────────────────────────────┘
```

- **The tool** is a launcher. It reads `migrate-mongo.yaml`, builds your migrations project, opens any
  [SSH tunnel](../guides/SSH-Tunnels.md), runs the project and renders what it reports. It never loads your code or the
  MongoDB driver.
- **Your migrations project** is a small console app that calls `MigrateMongoRunner.RunAsync(args)`. It contains the
  engine, your migrations and collection definitions, and the `MongoDB.Driver` version *you* chose. This is the only
  process that talks to MongoDB.
- **Your app** can run the same engine at startup with [Hosting](../guides/Running-from-Your-Application.md). It uses
  the same state collections and lock, so the CLI and app instances coordinate safely.

## A run, step by step

1. The tool finds `migrate-mongo.yaml`, applies `--env` and resolves `${VARIABLES}`.
2. It builds the project from `project:` with your .NET SDK (the one your `global.json` pins), unless you pass
   `--no-build` or a pre-built `--runner` such as a [bundle](../guides/Bundles.md).
3. **Handshake.** It runs the project with `--mm-handshake`. The project replies with the protocol versions it speaks
   and its library, driver and runtime versions, printed as the `Runner:` line. The tool picks the highest protocol
   version both understand.
4. It runs the project again with the command and the resolved settings on standard input, so connection strings and
   keys aren't on its command line.
5. The project takes the [lock](./Safety.md#locking), does the work, and streams events (`migrationStarted`,
   `lockWaiting`, `result`, ...) as JSON lines. The tool renders them, or passes them through with `--json`, and exits
   with the result's [exit code](../reference/Exit-Codes.md).

Running the project directly (`dotnet run -- up`) or a bundle skips the tool: the project reads the config itself and
writes the output.

## Why versions don't clash

Because the tool only starts processes and exchanges JSON, nothing is loaded across the boundary:

- **The tool doesn't need .NET.** It's compiled with Native AOT. The .NET SDK is only needed to *build* your project.
- **You choose the driver.** Your project references `MongoDB.Driver` 3.9 or later. Upgrading it doesn't need a new
  tool.
- **The tool and the libraries version independently.** The protocol is versioned. If the tool is too old for the
  runner, it stops with exit code `5` and tells you to update it, rather than failing with a confusing error.

## Finding migrations without reflection

A source generator in `Kevsoft.MigrateMongo.Abstractions` finds `[Migration]`, `[ManagedCollection]` and
`[ManagedView]` classes at compile time and writes the code that creates them (resolving constructor dependencies from
the service provider). That means:

- mistakes are [build errors](../reference/Build-Errors.md), not runtime surprises;
- nothing is discovered by reflection at runtime;
- a migration's checksum is computed from its source at build time. See [Safety](./Safety.md#checksums).

## See also

- [Packages](../reference/Packages.md)
- [Migrations vs Collection Definitions](./Migrations-vs-Collection-Definitions.md)

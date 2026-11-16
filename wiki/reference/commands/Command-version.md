Prints the tool's version, platform and runtime. Tool only.

```
$ migrate-mongo version
migrate-mongo/0.2.0 (linux-x64; aot)
```

`aot` means the Native AOT build, which needs no .NET runtime. A framework-dependent build shows the .NET runtime it's
running on instead, e.g. `migrate-mongo/0.2.0 (win-x86; .NET 10.0.12)`.

The versions of the library, driver and runtime in your migrations project are printed on the `Runner:` line when any
other command builds and runs it.

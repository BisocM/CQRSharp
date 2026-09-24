## What and why

<!-- What does this change, and what problem does it solve? Link the issue: "Fixes #123". -->

## Checklist

- [ ] Branched from `Release`; one logical change.
- [ ] `dotnet build CQRSharp.sln -c Release -warnaserror` passes with **zero warnings** (public members have XML docs).
- [ ] `dotnet test --project tests/CQRSharp.Tests` passes, and the change comes with tests.
- [ ] Docs under `docs/` and `CHANGELOG.md` are updated for any behavior or API change.
- [ ] Breaking change? It is called out as **breaking** in the changelog with a migration step — or: not breaking.
- [ ] `<Version>` in `Directory.Build.props` is **not** bumped (the maintainer does that when releasing).

When applicable:

- [ ] **Runtime code** reads time only through `TimeProvider`, and uses no runtime reflection in the AOT-compatible
      packages; a new feature there is exercised by `samples/CQRSharp.Sample` (the Native AOT canary).
- [ ] **Generator:** `IncrementalGeneratorCachingTests` is green and `GeneratedCodeCompilesTests` has a case for the new
      shape; Roslyn stays on 4.8.x.
- [ ] **Stores:** the shared contract suites (`OutboxStoreContractTests`, `InboxStoreContractTests`,
      `IdempotencyStoreContractTests`) pass for every store, and the Redis, PostgreSQL and SQL Server tests were run
      against real servers (or I am relying on CI for that).
- [ ] **Authoring surface:** new consumer-facing types are in the `CQRSharp` or `CQRSharp.Pipelines` namespace.
- [ ] **Dispatch path:** benchmarks were run (`benchmarks/CQRSharp.Benchmarks`) and the numbers are in the description.

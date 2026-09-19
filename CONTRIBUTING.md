# Contributing to CQRSharp

Issues and pull requests are welcome. CQRSharp is maintained by one person ([@BisocM](https://github.com/BisocM)), so
the most helpful contributions are small, focused, and arrive with tests. For anything larger than a bug fix, open an
issue first so the design can be agreed before you write the code.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md). **Do not report security vulnerabilities in
public issues** — see [SECURITY.md](SECURITY.md).

## Prerequisites

- A .NET SDK, **8.0.100 or later** (`global.json` rolls forward to the newest stable SDK you have installed; preview
  SDKs are not picked up).
- The projects choose their target frameworks from the SDK that builds them: `net8.0` with the .NET 8 SDK, plus
  `net9.0` with the .NET 9 SDK, plus `net10.0` with the .NET 10 SDK. CI builds with the 8.0.x SDK, so **`net8.0` is the
  gate every change must pass**; installing the 9 and 10 SDKs as well lets you check the other targets locally. Running
  the tests for a target needs that target's runtime installed.
- Optional: Docker (or a local Redis) for the Redis-backed tests, and the
  [Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites) if you want to
  publish the AOT canary locally.

## Build

```bash
dotnet build CQRSharp.sln -c Release -warnaserror
```

**Zero warnings is a gate**, locally and in CI: compiler warnings, missing XML docs, the trim/AOT analyzers and
CQRSharp's own analyzers all fail the build. Fix the cause rather than adding a `NoWarn` or a `#pragma`; if a
suppression really is right, scope it as narrowly as possible and say why in a comment.

## Test

```bash
dotnet test tests/CQRSharp.Tests
```

The Redis contract tests connect to `localhost:6379` (override with the `CQRSHARP_TEST_REDIS` environment variable) and
**skip cleanly when no server is reachable**, so a plain `dotnet test` works on any machine. To run them for real:

```bash
docker run -d -p 6379:6379 redis:7-alpine
```

CI always runs them against a Redis service container, so a change to `CQRSharp.Redis` that you could not test locally
is still tested before it merges. The EF Core store tests run against SQLite and need nothing installed.

### Sample self-test and the Native AOT canary

`samples/CQRSharp.Sample` runs an end-to-end self-test on startup (dispatch, the pipeline, notifications, an outbox
round-trip, idempotency, rate limiting, streaming, exception handling, a second handler assembly) and exits non-zero if
any part fails. CI runs it on every pull request:

```bash
dotnet run --project samples/CQRSharp.Sample -c Release -f net8.0
```

The same project is the **Native AOT canary**. The release pipeline publishes it with Native AOT, fails on any trim/AOT
(`IL####`) warning attributed to CQRSharp source, and runs the native binary. To reproduce locally (substitute your
runtime identifier):

```bash
dotnet publish samples/CQRSharp.Sample -c Release -f net8.0 -r linux-x64 -o artifacts/aot
./artifacts/aot/CQRSharp.Sample
```

If your change adds a feature to an AOT-compatible package, exercise it from the sample so the canary covers it.

### Benchmarks

`benchmarks/CQRSharp.Benchmarks` is deliberately **not** in `CQRSharp.sln` (it references third-party mediators the
library must never depend on) and does not run in CI. Run it on demand, on a quiet machine, when you touch the dispatch
path:

```bash
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks             # everything (~8 minutes)
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter "*Request_*" --job short
```

See [benchmarks/README.md](benchmarks/README.md) for the method. The MediatR reference there is pinned to 12.5.0, the
last Apache-2.0 release — do not bump it.

## Repository layout

```
src/
  CQRSharp                       meta-package: no lib/, embeds the generator + analyzers, ships the global usings
  CQRSharp.Abstractions          contracts (netstandard2.0 + net8.0 and up)
  CQRSharp.Core                  the runtime: dispatcher, pipeline execution, notifications, queue, outbox processor
  CQRSharp.Pipelines             the opt-in behaviors and the fluent builder
  CQRSharp.Generators            the Roslyn source generator (netstandard2.0)
  CQRSharp.Analyzers             the CQRA analyzers and code fixes (netstandard2.0)
  CQRSharp.Redis                 Redis outbox + idempotency stores (AOT-compatible)
  CQRSharp.EntityFrameworkCore   EF Core outbox + idempotency stores (not AOT-compatible, by design)
  CQRSharp.FluentValidation      FluentValidation integration (not AOT-compatible, by design)
  CQRSharp.AspNetCore            ASP.NET Core result / ProblemDetails mapping
  CQRSharp.Testing               the store contract suites + RecordingCqrsDispatcher, shipped for consumers' tests
  Shared                         source linked into the generator, the analyzers and the tests
samples/      CQRSharp.Sample (self-test + AOT canary), .Sample.Minimal, .Sample.ExternalModule
tests/        CQRSharp.Tests (the suite), CQRSharp.Tests.ExternalModule (second-assembly fixture)
benchmarks/   BenchmarkDotNet comparison (not in the solution)
templates/    the `dotnet new cqrsharp` template
docs/         the documentation (start at docs/README.md)
```

## The rules that matter here

- **Every public member has XML docs.** The packages build with `GenerateDocumentationFile`, so a missing doc comment
  is a warning, and a warning fails the build.
- **All time reads go through `TimeProvider`.** Do not add a `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Stopwatch` or
  `Environment.TickCount` read to the runtime, the behaviors or a store: take the injected `TimeProvider`, and measure
  durations with `GetTimestamp()` / `GetElapsedTime()`. Tests drive time with `FakeTimeProvider`; a test that sleeps to wait for a timeout is a bug.
- **No runtime reflection in the AOT-compatible packages** (`CQRSharp.Abstractions`, `.Core`, `.Pipelines`, `.Redis`,
  `.AspNetCore`). No `MakeGenericType`, no assembly scanning, no reflection-based `JsonSerializer`. If something needs
  type information at runtime, the generator should emit it. The trim/AOT analyzers enforce this at build time and the
  AOT canary enforces it for the whole graph.
- **Generator changes** must keep `IncrementalGeneratorCachingTests` green — the pipeline flows value-equatable models
  only; never carry a `Compilation` or a symbol into the output step — and must add a case to
  `GeneratedCodeCompilesTests` proving the emitted code compiles for the new shape. The generator and analyzers
  reference `Microsoft.CodeAnalysis` **4.8.x on purpose**, so they load in older compilers and IDEs; do not bump it and
  do not use newer Roslyn APIs.
- **Public API changes are recorded.** Every library lists its public surface in `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt`. Adding a public member without recording it is a build error (RS0016) — apply the IDE
  fix ("Add to public API"), which writes it to `PublicAPI.Unshipped.txt`, and commit that with the change. Removing or
  changing a *shipped* member is a breaking change and needs a major version.
- **Package versions live in `Directory.Packages.props`** (central package management); a `PackageReference` in a
  project file carries no `Version`.
- **Store changes** must pass the shared contract suites in `src/CQRSharp.Testing`
  (`OutboxStoreContractTests`, `IdempotencyStoreContractTests`). A new store derives from both; a new guarantee is added
  to the suite first, so the in-memory, Redis and EF Core stores all have to meet it.
- **New authoring-surface types go in the `CQRSharp` or `CQRSharp.Pipelines` namespace**, whichever folder or assembly
  they live in. Those two namespaces are everything a consumer writes against (and are the meta-package's global
  usings); runtime internals stay under `CQRSharp.Core.*`.
- **Behavior changes are documented.** Update the relevant page under `docs/` and add an entry to `CHANGELOG.md`;
  call a breaking change out as breaking, with the migration step.

### Code style

`.editorconfig` encodes it; in short: file-scoped namespaces, 4-space indentation, `var`, expression-bodied members
where they fit on a line, primary constructors and collection expressions where they read well, private fields
`_camelCase`, no `#region`s. Comments explain **why**, not what. The style rules are suggestions rather than build
errors — match the code around you.

## Branching and pull requests

1. Branch from **`Release`** (the default branch) and open your pull request back into `Release`.
2. CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) must be green: the zero-warning build, the full test
   suite including the Redis contract tests, and the sample self-test.
3. Keep a pull request to one logical change and fill in the template.
4. **Do not bump `<Version>`** in `Directory.Build.props`, and do not edit `PackageReleaseNotes` — the maintainer does
   both when cutting a release. A push to `Release` runs the publish pipeline
   ([`.github/workflows/nuget_publish.yml`](.github/workflows/nuget_publish.yml)), which publishes to NuGet only when
   the version is one NuGet has not seen, so merging an ordinary pull request does not publish anything.

## Commit messages

An imperative subject that says what the commit does, no trailing period, no `feat:` / `fix:` prefixes:

```
Idempotency replays the original result instead of rejecting a completed duplicate
```

Add a body, wrapped at about 120 columns, when the *why* is not obvious from the subject — what was wrong, what the
change guarantees now, and anything a reader of `git blame` will want to know.

## Reporting bugs and requesting features

Use the [issue forms](https://github.com/BisocM/CQRSharp/issues/new/choose). A bug report is most useful with the
package versions, the .NET version, the store in use, whether Native AOT or trimming is involved, and a minimal
reproduction; if an analyzer, the generator or the startup validator reported something, include the `CQRA` / `CQRGEN` /
`CQRCONF` id.

Security vulnerabilities go through [private reporting](SECURITY.md), never a public issue.

## License

CQRSharp is [MIT licensed](LICENSE); contributions are accepted under the same license.

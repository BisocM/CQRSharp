# Contributing to CQRSharp

Issues and pull requests are welcome. CQRSharp is maintained by one person ([@BisocM](https://github.com/BisocM)), so
the most helpful contributions are small, focused, and arrive with tests. For anything larger than a bug fix, open an
issue first so the design can be agreed before you write the code.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md). **Do not report security vulnerabilities in
public issues** — see [SECURITY.md](SECURITY.md).

## Prerequisites

- The **.NET 10 SDK**. `global.json` asks for 10.0.100 and rolls forward to the newest stable SDK you have installed
  (preview SDKs are not picked up). The 10 SDK builds every target framework, `net8.0`, `net9.0` and `net10.0`.
- The **.NET 8 and 9 runtimes** as well, to run the tests of those targets. `dotnet test` runs all three.
- Optional: **Docker**, for the Redis, PostgreSQL and SQL Server tests (see [Test](#test)), and the
  [Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites) to publish
  the AOT canary locally.

## Build

```bash
dotnet build CQRSharp.sln -c Release -warnaserror
python3 .github/scripts/check_logging.py
python3 .github/scripts/check_links.py
```

**Zero warnings is a gate**, locally and in CI: compiler warnings, missing XML docs, the trim/AOT analyzers, the public
API analyzers, the banned-API list and CQRSharp's own analyzers all fail the build. Fix the cause rather than adding a
`NoWarn` or a `#pragma`; if a suppression really is right, scope it as narrowly as possible and say why in a comment.
`check_logging.py` checks the [logging contract](#logging); CI runs it first. `check_links.py` checks that every relative
link between the Markdown files reaches an existing file and heading. Link to docs by relative path, never to a branch on
GitHub: the packed README gets its links pinned to the version's tag at pack time.

## Test

```bash
dotnet test --project tests/CQRSharp.Tests --hangdump --hangdump-timeout 5m --hangdump-type Mini
```

The tests are xUnit v3 on Microsoft.Testing.Platform (`global.json` opts `dotnet test` into it). Keep the hang-dump
options: a test stuck for five minutes then fails with a dump instead of hanging the run. To iterate on one area, build
once and run one framework and a few classes:

```bash
dotnet build tests/CQRSharp.Tests/CQRSharp.Tests.csproj -c Release -warnaserror
dotnet test --project tests/CQRSharp.Tests/CQRSharp.Tests.csproj -c Release --no-build --framework net8.0 \
  --hangdump --hangdump-timeout 5m --hangdump-type Mini --filter-class "*OutboxProcessorTests"
```

`--filter-class` can be repeated, and `--filter-namespace` selects an area (`CQRSharp.Tests.Core`, …).

### Tests that need a server

The Redis store tests and the EF Core store tests on PostgreSQL 16 and SQL Server 2022 each take their server from an
environment variable, or start one with [Testcontainers](https://dotnet.testcontainers.org/):

| Tests | Environment variable | Testcontainers image |
| --- | --- | --- |
| Redis stores | `CQRSHARP_TEST_REDIS` (a StackExchange.Redis connection string) | `redis:7-alpine` |
| EF Core stores on PostgreSQL | `CQRSHARP_TEST_POSTGRES` (an Npgsql connection string) | `postgres:16-alpine` |
| EF Core stores on SQL Server | `CQRSHARP_TEST_SQLSERVER` (a SqlClient connection string) | `mcr.microsoft.com/mssql/server:2022-latest` |

- **Variable set:** the tests use that server. A Redis server that cannot be reached fails the Redis tests rather
  than skipping them. On a PostgreSQL or SQL Server named this way, each test process creates and owns the database
  `<database>_net<major>` (for example `cqrsharp_net8`), dropping an earlier run's copy first, so the account needs
  the right to create databases.
- **Variable unset, Docker available:** the fixture starts a container, private to the test process. The first run
  pulls the images (the SQL Server one is large), and a container may take up to 4 minutes to start.
- **Neither:** those tests skip, with the reason, so a plain `dotnet test` works on any machine.

The EF Core stores also run on SQLite, which needs nothing installed. CI's `validate` job
([`.github/workflows/validate.yml`](.github/workflows/validate.yml)) runs every one of these tests against service
containers, so a change to a store you could not test locally is still tested before it merges. The Windows and macOS
jobs have no service containers.

### Writing tests

- **Deterministic, always.** Drive time with `FakeTimeProvider`; wait on signals (`TaskCompletionSource`, the outbox
  harness's `DrainAsync` / `SettleAsync` in `tests/CQRSharp.Tests/Core/Outbox/OutboxTestHarness.cs`), never on the real
  clock: no `Task.Delay`, no `WaitAsync(TimeSpan)` guard, no timing thresholds.
- **Every bug fix gets a regression test** that fails without the fix.
- **The layout mirrors production.** A test lives at `tests/CQRSharp.Tests/<Area>/<Feature>/…`, is named for what it
  tests, and uses its area's namespace (`CQRSharp.Tests.Core`, `CQRSharp.Tests.Pipelines`, …). Shared fakes live in
  `tests/CQRSharp.Tests/Shared`.
- **The generator runs over the test assembly**, so it registers every closed `IRequestValidator<T>` and exception hook
  declared there: make a shared fake generic or a private nested type if it must not be picked up.
- **Roslyn test compilations** take their references from `ProbeReferences.Create()`.
- **Redis tests** isolate themselves by key prefix (`RedisFixture.NewKeyPrefix(...)`), because the three target
  frameworks' test processes can share one server.

### Samples and the Native AOT canary

Two samples run a self-test and exit non-zero when a scenario fails. CI runs both on every pull request, on Linux,
Windows and macOS:

```bash
dotnet run --project samples/CQRSharp.Sample -c Release -f net8.0
dotnet run --project samples/CQRSharp.Sample.AspNetCore -c Release -f net8.0 -- --self-test
```

`CQRSharp.Sample` covers the runtime end to end (dispatch, every pipeline behavior, the outbox, idempotency replay,
queued dispatch, streams, exception hooks, a second handler assembly, diagnostics); `CQRSharp.Sample.AspNetCore` covers
the HTTP edge. Both are also the **Native AOT canary**: the `aot-canary` job publishes them as native binaries for
`net8.0` and `net10.0`, fails on any trim/AOT (`IL####`) warning in the publish, dependencies included, and runs them.
To reproduce locally (substitute your runtime identifier):

```bash
dotnet publish samples/CQRSharp.Sample -c Release -f net8.0 -r linux-x64 -o artifacts/aot/sample
./artifacts/aot/sample/CQRSharp.Sample
dotnet publish samples/CQRSharp.Sample.AspNetCore -c Release -f net8.0 -r linux-x64 -o artifacts/aot/web
(cd artifacts/aot/web && ./CQRSharp.Sample.AspNetCore --self-test)   # from its output directory, as CI runs it
```

If your change adds a feature to an AOT-compatible package, exercise it from a sample so the canary covers it.

### Benchmarks

`benchmarks/CQRSharp.Benchmarks` is deliberately **not** in `CQRSharp.sln` (it references third-party mediators the
library must never depend on) and does not run in CI. Run it on a quiet machine when you touch the dispatch path;
[benchmarks/README.md](benchmarks/README.md) has the commands, the method, and the rule for publishing results. The
MediatR reference is pinned to 12.5.0, the last Apache-2.0 release; do not bump it.

## Repository layout

```
src/
  CQRSharp                       meta-package: no lib/, embeds the source generator, ships the global usings
  CQRSharp.Abstractions          contracts (netstandard2.0 + net8.0 and up); carries the analyzers to every consumer
  CQRSharp.Core                  the runtime: dispatcher, pipeline execution, notifications, queue, outbox processor
  CQRSharp.Pipelines             the pipeline behaviors and the fluent builder
  CQRSharp.Generators            the Roslyn source generator (netstandard2.0)
  CQRSharp.Analyzers             the CQRA analyzers and code fixes (netstandard2.0)
  CQRSharp.Redis                 Redis outbox, inbox and idempotency stores (AOT-compatible)
  CQRSharp.EntityFrameworkCore   EF Core stores and unit of work (not AOT-compatible, by design)
  CQRSharp.FluentValidation      FluentValidation integration (not AOT-compatible, by design)
  CQRSharp.AspNetCore            ASP.NET Core result, ProblemDetails and Idempotency-Key mapping
  CQRSharp.Testing               RecordingCqrsDispatcher, for consumers' tests (no test framework)
  CQRSharp.Testing.Xunit.V3      the store contract suites, for consumers' tests (xUnit v3)
  Shared                         source linked into the generator, the analyzers and the tests
samples/      CQRSharp.Sample (self-test + AOT canary), .Sample.AspNetCore (HTTP self-test + AOT canary),
              .Sample.ExternalModule (a second handler assembly)
tests/        CQRSharp.Tests (the suite), CQRSharp.Tests.ExternalModule (second-assembly fixture)
benchmarks/   BenchmarkDotNet comparison (not in the solution)
templates/    the `dotnet new cqrsharp` template (CQRSharp.Templates)
docs/         the documentation (start at docs/README.md)
.github/      workflows, and the scripts they run (package validation, the logging and link checks, release notes)
```

## The rules that matter here

- **Every public member has XML docs.** The packages build with `GenerateDocumentationFile`, so a missing doc comment
  is a warning, and a warning fails the build.
- **All time reads go through `TimeProvider`.** Do not add a `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Stopwatch` or
  `Environment.TickCount` read to the runtime, the behaviors or a store: take the injected `TimeProvider`, and measure
  durations with `GetTimestamp()` / `GetElapsedTime()`.
- **Library code never resumes on its caller's context.** Every `await` under `src/` is `.ConfigureAwait(false)`,
  including `await using (x.ConfigureAwait(false))` and `await foreach (… in x.ConfigureAwait(false))`;
  `src/.editorconfig` makes CA2007 a build error (the xUnit suites in `CQRSharp.Testing.Xunit.V3` are exempt).
  `Task.Yield` is banned (`src/BannedSymbols.txt`, RS0030): use
  `await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding)`.
- **No runtime reflection in the AOT-compatible packages** (`CQRSharp.Abstractions`, `.Core`, `.Pipelines`, `.Redis`,
  `.AspNetCore`). No `MakeGenericType`, no assembly scanning, no reflection-based `JsonSerializer`. If something needs
  type information at runtime, the generator should emit it. The trim/AOT analyzers enforce this at build time and the
  AOT canary checks what the two samples exercise.
- **Generator changes** must keep `IncrementalGeneratorCachingTests` green — the pipeline flows value-equatable models
  only; never carry a `Compilation` or a symbol into the output step — and must add a case to
  `GeneratedCodeCompilesTests` proving the emitted code compiles for the new shape. The generator and analyzers
  reference `Microsoft.CodeAnalysis` **4.8.x on purpose**, so they load in older compilers and IDEs; do not bump it and
  do not use newer Roslyn APIs.
- **Package versions live in `Directory.Packages.props`** (central package management); a `PackageReference` in a
  project file carries no `Version`.
- **Store changes** must pass the shared contract suites in `src/CQRSharp.Testing.Xunit.V3`
  (`OutboxStoreContractTests`, `InboxStoreContractTests`, `IdempotencyStoreContractTests`). A new store derives from
  them; a new guarantee is added to the suite first, so the in-memory, Redis and EF Core stores all have to meet it.
- **Behavior changes are documented.** Update the page under `docs/` that owns the topic and add an entry to
  `CHANGELOG.md`; call a breaking change out as breaking, with a numbered migration step.

### Namespaces and folders

- **Pick the namespace by audience, whichever folder or assembly the type lives in.** Everyday application code
  (requests, handlers, results, notifications, exceptions callers catch, options) goes in `CQRSharp`; pipeline
  authoring and configuration in `CQRSharp.Pipelines`; contracts an infrastructure implementer writes against (stores,
  unit of work, serializers) in `CQRSharp.Persistence`. The first two are the meta-package's global usings, so a public
  name there must not repeat a well-known framework type (that is why the limiter is `RequestRateLimiter`, not
  `RateLimiter`). Each integration package has one namespace (`CQRSharp.Redis`, `CQRSharp.EntityFrameworkCore`, …), and
  its registration extensions go in `Microsoft.Extensions.DependencyInjection`. Runtime extension points stay under
  `CQRSharp.Core.<Feature>` (`Outbox`, `BackgroundTasks`, `Registries`, `Pipelines`, …).
- **A public type that exists only for generated code** gets `[EditorBrowsable(EditorBrowsableState.Never)]`.
- **Organize folders by feature, not by kind.** A project's top-level folders are its features (`Outbox/`,
  `Notifications/`, `Idempotency/`, …), split further into their concerns once a folder stops being one cohesive group
  (`Outbox/Buffering`, `Outbox/Processing`, `Outbox/InMemory`). An option type or enum lives next to the feature it
  configures; there is no `Options/`, `Interfaces/`, `Models/` or `Types/` folder. Folders only organize files: a
  file's namespace comes from the rule above, not from its path.

### Logging

Every log line in `src/` is a `[LoggerMessage]` source-generated method with an event id, so the ids and property names
are a stable contract operators filter and alert on, and a disabled level costs nothing. `check_logging.py` fails on a
direct `ILogger.Log*` call, a `[LoggerMessage]` without an event id, and two messages of one assembly sharing an id.

Each component owns a block of 100 ids; a new message takes the next free id of its component's block:

| Block | Component | Assembly |
| --- | --- | --- |
| 1000 | Dispatch (the pipeline executor) | CQRSharp.Core |
| 1100 | Background task queue | CQRSharp.Core |
| 1200 | Startup validation and diagnostics | CQRSharp.Core |
| 4000 | Logging behavior | CQRSharp.Pipelines |
| 4100 | Idempotency behaviors | CQRSharp.Pipelines |
| 4200 | Unit-of-work behaviors | CQRSharp.Pipelines |
| 4300 | Resilience behaviors | CQRSharp.Pipelines |
| 4400 | Timeout behaviors | CQRSharp.Pipelines |
| 4500 | Rate-limiting behaviors | CQRSharp.Pipelines |
| 4600 | Exception-handling behaviors | CQRSharp.Pipelines |
| 4700 | Validation behaviors (reserved) | CQRSharp.Pipelines |
| 5000 | Outbox processor | CQRSharp.Core |
| 5100 | Outbox buffering and the in-memory stores (reserved) | CQRSharp.Core |
| 6000 | EF Core stores | CQRSharp.EntityFrameworkCore |
| 6100 | Redis stores (reserved) | CQRSharp.Redis |
| 7000 | ASP.NET Core integration | CQRSharp.AspNetCore |

The ids in use are listed in [docs/observability.md](docs/observability.md); a new one goes there too. Use one property
name per datum: `{RequestName}` (`typeof(TRequest).Name`), `{NotificationName}`, `{NotificationType}` (the stable
outbox name), `{MessageId}`, `{HandlerName}`, `{UserId}`, `{ElapsedMs}`, `{Attempt}`, `{ExceptionType}`. Choose the
level by who has to act: `Error` for what needs an operator, `Warning` for a failure that is repaired later or a request
the server could not serve, `Information` for an outcome the caller has to deal with, `Debug` for routine per-message
detail.

### Public API

Every library lists its public surface in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, checked by the public
API analyzers:

- Adding a public member without recording it is a build error (RS0016). Apply the IDE fix ("Add to public API"), which
  writes the entry to `PublicAPI.Unshipped.txt`, and commit it with the change.
- Removing or changing a recorded member is a build error too (RS0017) until its entry is removed. Removing or changing
  a member that is in `PublicAPI.Shipped.txt` is a breaking change and needs a major version.
- When a version is released, the maintainer moves the `Unshipped` entries into `Shipped`, so `Unshipped` lists exactly
  what the next release adds.
- Package validation runs on pack and checks that every target framework exposes a compatible surface.

### Code style

`.editorconfig` encodes it; in short: file-scoped namespaces, 4-space indentation, `var`, expression-bodied members
where they fit on a line, primary constructors and collection expressions where they read well, private fields
`_camelCase`, no `#region`s. Comments explain **why**, not what, and describe the code as it is (no "used to" or
"now"). The style rules are suggestions rather than build errors — match the code around you.

## Branching and pull requests

1. Branch from **`Release`** (the default branch) and open your pull request back into `Release`.
2. CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs on every pull request and must be green. It runs the
   shared gate in [`validate.yml`](.github/workflows/validate.yml) — the logging check, the zero-warning build, the
   tests on all three frameworks with Redis, PostgreSQL and SQL Server service containers, both samples' self-tests,
   pack with package validation, a template smoke test against the packed packages, and the Native AOT canary — and
   builds and tests on Windows and macOS. Run the workflow by hand to validate a branch that has no pull request yet.
3. Keep a pull request to one logical change and fill in the template.
4. **Do not bump `<Version>`** in `Directory.Build.props`, and do not edit `PackageReleaseNotes` — the maintainer does
   both when cutting a release.

A push to `Release` runs the release pipeline
([`.github/workflows/nuget_publish.yml`](.github/workflows/nuget_publish.yml)): the same gate, then a publish to NuGet
only when the version is one NuGet has not seen, so merging an ordinary pull request publishes nothing. Only a commit of
`Release` is ever published or tagged: a publishing run dispatched by hand on another branch fails at once, and a run
with `dry_run` ticked (allowed on any branch) runs the gate and an advisory release review but never publishes or tags.
A published version is tagged `v<version>`, and its GitHub release notes are the `## [<version>]` section of
`CHANGELOG.md`: keep that heading format, because the pipeline fails before publishing when the section is missing.

## Commit messages

An imperative subject that says what the commit does, no trailing period, no `feat:` / `fix:` prefixes:

```
Idempotency replays the original result instead of rejecting a completed duplicate
```

Add a body, wrapped at about 120 columns, when the *why* is not obvious from the subject — what was wrong, what the
change guarantees, and anything a reader of `git blame` will want to know.

## Reporting bugs and requesting features

Use the [issue forms](https://github.com/BisocM/CQRSharp/issues/new/choose). A bug report is most useful with the
package versions, the .NET version, the store in use, whether Native AOT or trimming is involved, and a minimal
reproduction; if an analyzer, the generator or the startup validator reported something, include the `CQRA` / `CQRGEN` /
`CQRCONF` / `CQRDIAG` id.

Security vulnerabilities go through [private reporting](SECURITY.md), never a public issue.

## License

CQRSharp is [MIT licensed](LICENSE); contributions are accepted under the same license.

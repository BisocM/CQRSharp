# Native AOT

CQRSharp is built for Native AOT and trimming. The source generator writes dispatch and registration as plain C#, so
there is no runtime reflection, no `MakeGenericType`, no reflection-based handler lookup, and no reflection-based JSON.

- [What makes it AOT-safe](#what-makes-it-aot-safe)
- [What is and isn't AOT-safe](#what-is-and-isnt-aot-safe)
- [Value-type results and notifications](#value-type-results-and-notifications)
- [Publishing with Native AOT](#publishing-with-native-aot)
- [Trimming](#trimming)
- [Known issue: System.Text.Json 9 on net8.0](#known-issue-systemtextjson-9-on-net80)
- [Verifying](#verifying)

## What makes it AOT-safe

- **Generated dispatch.** Handler registrations, route tables and typed handler invokers are emitted as C# over closed
  types; nothing is constructed generically at runtime.
- **A hand-written outbox serializer.** Durable notifications are read and written by a generated serializer that works
  directly on `Utf8JsonReader` and `Utf8JsonWriter`, not by reflection-based `JsonSerializer` (which raises `IL2026` and
  `IL3050`). [The source generator](source-generator.md#the-outbox-serializer-and-the-fingerprinter) explains why
  System.Text.Json's own generator cannot be used for it.
- **Analyzer-enforced.** `CQRSharp.Abstractions`, `CQRSharp.Core`, `CQRSharp.Pipelines`, `CQRSharp.Redis` and
  `CQRSharp.AspNetCore` set `IsAotCompatible`, which turns on the trim and AOT analyzers, and the repository builds with
  warnings as errors, so AOT-hostile code in them fails the build.

## What is and isn't AOT-safe

| Component | Native AOT |
| --- | --- |
| Dispatch, the pipeline, notifications, the in-memory stores (`CQRSharp.Core`) | ✅ (value-type results: see [below](#value-type-results-and-notifications)) |
| The generated code | ✅ |
| The built-in behaviors (`CQRSharp.Pipelines`) | ✅ |
| `CQRSharp.Redis` stores | ✅ |
| `CQRSharp.AspNetCore` | ✅, see [ASP.NET Core](aspnetcore.md#native-aot) |
| Idempotency result replay (`ReplayResultsWith`) | ✅ with a source-generated `JsonSerializerContext` that lists your result types |
| `CQRSharp.EntityFrameworkCore` | ❌ EF Core compiles queries at runtime (`[RequiresDynamicCode]`) |
| `CQRSharp.FluentValidation` | ❌ FluentValidation builds its rules from expression trees; use `IRequestValidator<T>` under AOT |
| A custom serializer that uses reflection-based `JsonSerializer` | ❌ |
| A custom store or behavior that uses reflection or `MakeGenericType` | ❌ (your code) |

Under Native AOT, use the Redis stores (or an AOT-safe store of your own) instead of EF Core, and keep your handlers and
behaviors free of reflection. The AOT analyzers report violations in your own code when the project sets `PublishAot`.

## Value-type results and notifications

Without dynamic code, Microsoft's DI container cannot close an open-generic registration over a value type. That affects
behaviors:

- an open-generic `IPipelineBehavior<,>` or `IStreamPipelineBehavior<,>` for a request whose **result or streamed item is
  a value type** (`IQuery<int>`, `IStreamRequest<Guid>`);
- an open-generic `INotificationPipelineBehavior<>` for a **struct notification**.

For these, the generator emits a closed factory for each open-generic behavior it can see: those declared in the
assembly, CQRSharp's own, and those of referenced assemblies that reference `CQRSharp.Core` (where the behavior interfaces
live). The composing assembly also closes its own behaviors over the requests and struct notifications of the assemblies
it references. At runtime, the closed set is built from the final service collection: every registration of the behavior,
in registration order and with its registered lifetime, whether it was registered before or after `AddCqrsGenerated`.

A registered behavior the generated code **cannot** close is not skipped: dispatching the affected request, or
publishing or delivering the affected notification, throws `InvalidOperationException` naming the behavior, the request
or notification, and the reason. For a request, `ICqrsDiagnostics` and the startup validator also report it as
**CQRDIAG004**. Generated code cannot close a behavior that is:

- internal to an assembly that does not grant the application `InternalsVisibleTo`, private, or file-local;
- nested in a generic type;
- marked `[Obsolete]` as an error (a warning-level `[Obsolete]` or `[Experimental]` behavior is closed);

or when the request or notification itself cannot be named there. A behavior whose constraints exclude the value type
(`where TResult : class`) does not apply and does not run. The remaining limits: a behavior over a referenced assembly's
internal constructed generic request cannot be identified and is not reported, and a constructed generic struct
notification (`Wrapper<int>`) that no assembly running the generator handles gets no closed behaviors.

On the JIT none of this applies: the container closes open-generic behaviors itself.

## Publishing with Native AOT

Enable AOT in the executable project and publish for a runtime identifier:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

```bash
dotnet publish -c Release -r linux-x64
# or win-x64, osx-arm64, ...
```

CQRSharp's own code raises no trim or AOT (`IL`) warnings in a publish. A warning you see comes from your code or from a
dependency that is not AOT-compatible, commonly the EF Core integration.

## Trimming

The generated registrations name every handler, context factory, dispatcher route and serializer by its concrete type, so
the trimmer keeps what is reachable, and nothing needs `[DynamicDependency]` or a trimming root descriptor.

## Known issue: System.Text.Json 9 on net8.0

On net8.0, the System.Text.Json 9.x package (which `Microsoft.Extensions.*` 9.x packages bring in) reports `IL2090` in its
enum converter during a Native AOT publish when a source-generated `JsonSerializerContext` serializes an enum.
`ReplayResultsWith` with a `CommandResult<T>` result always does, through `ErrorKind`. Staying on the
`Microsoft.Extensions.*` 8.x packages on net8.0 (which use the in-box System.Text.Json 8), or referencing System.Text.Json
10.x, avoids it. It was reproduced with System.Text.Json 9.0.2 and 9.0.13; 10.0.3 and the in-box 8.0 publish cleanly.

## Verifying

The repository publishes two samples as Native AOT binaries for net8.0 and net10.0 and runs them, in the `aot-canary` job
of `.github/workflows/validate.yml`; any `IL` warning in either publish, dependencies included, fails the job.
`CQRSharp.Sample` runs an end-to-end self-test of dispatch, every built-in behavior, the transactional outbox,
notification serialization, idempotency replay, queued dispatch, streams, exception hooks, a second assembly's module,
and the diagnostics API. `CQRSharp.Sample.AspNetCore` runs HTTP round trips through the `CQRSharp.AspNetCore` result and
exception mapping and the `Idempotency-Key` header. To verify your own app, publish it with `PublishAot=true` and check
for a warning-free publish and a working run.

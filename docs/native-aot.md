# Native AOT

CQRSharp is designed **AOT-first**. The source generator emits all dispatch and registration as plain
C#, so there is no runtime reflection, no `MakeGenericType`, no reflection-based handler lookup, and no
reflection-based JSON. The libraries build under the .NET AOT analyzers with warnings-as-errors.

- [What makes it AOT-safe](#what-makes-it-aot-safe)
- [What is and isn't AOT-safe](#what-is-and-isnt-aot-safe)
- [Publishing with Native AOT](#publishing-with-native-aot)
- [Trimming](#trimming)
- [AOT hints](#aot-hints)
- [Verifying](#verifying)

## What makes it AOT-safe

- **Source-generated dispatch.** The handler/dispatcher/registry tables are emitted as concrete,
  statically-typed C#. Dispatch is a generated switch/lookup over closed types — no generic construction
  at runtime.
- **Hand-rolled outbox serialization.** Notifications routed through the outbox are (de)serialized by a
  generated serializer written directly over `Utf8JsonReader`/`Utf8JsonWriter` — no
  `JsonSerializer`-by-reflection (which is `IL2026`/`IL3050`). System.Text.Json's own source generator
  can't be used here because a `[JsonSerializable]` context CQRSharp emits is invisible to STJ's
  generator (Roslyn runs every generator against the user's original syntax), so the serializer is
  emitted by hand.
- **Analyzer-enforced.** `CQRSharp.Core`, `CQRSharp.Pipelines`, `CQRSharp.Abstractions`, and
  `CQRSharp.Redis` set `IsAotCompatible` and enable the trim/AOT/single-file analyzers; the solution
  promotes any AOT-hostile pattern to a build error.

## What is and isn't AOT-safe

| Component | AOT-safe |
| --- | --- |
| `CQRSharp` core (dispatch, pipeline, notifications, in-memory stores) | ✅ |
| The source generator's output | ✅ |
| `CQRSharp.Pipelines` behaviors | ✅ |
| `CQRSharp.Redis` stores | ✅ |
| `CQRSharp.EntityFrameworkCore` stores | ❌ — EF Core uses runtime query compilation (`[RequiresDynamicCode]`) |
| A custom serializer using reflection-based `JsonSerializer` | ❌ |
| A custom store/behavior that uses reflection or `MakeGenericType` | ❌ (your code) |

When publishing with Native AOT, choose the Redis store (or a custom AOT-safe store) over EF Core, and
keep your handlers and any custom behaviors reflection-free. The AOT analyzers will flag violations in
your own code if you reference the framework from an AOT-enabled project.

## Publishing with Native AOT

Enable AOT in the executable project and publish for a runtime identifier:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

```bash
dotnet publish -c Release -r win-x64 -p:PublishAot=true
# or linux-x64 / osx-arm64 / ...
```

A clean publish produces a native binary with **no IL trim/AOT warnings**. If you see `IL2026`/`IL3050`
warnings, they come from your code or a non-AOT dependency (commonly the EF Core integration) — not from
CQRSharp's own surface.

## Trimming

The generated registrations reference every handler, context factory, dispatcher, and serializer by
concrete type, so the trimmer keeps exactly what's reachable and nothing needs `[DynamicDependency]` or
a trimming root descriptor. The framework is trim-safe at the default aggressive trim level.

## AOT hints

A companion generator (`CqrsAotHintGenerator`) emits an `AotHintProvider` into your assembly — a small
set of compile-time hints that help the AOT compiler and keep generic instantiations rooted. It is part
of the generated output; you don't reference it directly.

## Verifying

The repository's sample (`CQRSharp.Sample`) is the AOT canary: it sets `PublishAot=true`, references the
Redis integration so its IL is exercised, and runs an end-to-end self-test on startup that drives
dispatch, the pipeline, notifications, an outbox round-trip, idempotency, rate limiting, streaming, and
exception handling. The CI publishes it with Native AOT and runs it, so an AOT regression fails the
build. To verify your own app, publish it with `PublishAot=true` and confirm a clean build plus a
working run.

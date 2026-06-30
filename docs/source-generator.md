# The source generator

Everything CQRSharp does at runtime is wired up at **compile time** by two Roslyn generators. This page
explains what they emit and how they stay AOT-safe and incremental. You don't need any of this to *use*
CQRSharp — it's here for the curious and for contributors.

- [What gets generated](#what-gets-generated)
- [The well-known-type handoff](#the-well-known-type-handoff)
- [The incremental pipeline](#the-incremental-pipeline)
- [The outbox serializer](#the-outbox-serializer)
- [Cross-assembly discovery](#cross-assembly-discovery)

## What gets generated

`CqrsSourceGenerator` scans the compilation for requests, handlers, contexts, and notifications, and
emits these sources into your assembly:

| Generated source | Contains |
| --- | --- |
| `CqrsGeneratedRegistrar.g.cs` | `AddCqrsGenerated(...)` — the DI registration of every handler, dispatcher, registry, and (when configured) the outbox/idempotency wiring. |
| `CqrsGeneratedBootstrap.g.cs` | The builder-overload entry point that constructs the fluent `CqrsBuilder` and applies the generated registrations in the correct order. |
| `GeneratedRequestDispatcher.g.cs` | The concrete `Send` dispatch table — a typed lookup from request to handler, with the behavior pipeline and lifecycle notifications. |
| `GeneratedStreamRequestDispatcher.g.cs` | The same for `Stream`. |
| `GeneratedDirectNotificationDispatcher.g.cs` | The typed notification fan-out used by `Publish`. |
| `GeneratedCqrsDiagnostics.g.cs` | The `ICqrsDiagnostics` implementation that describes each request binding. |
| `GeneratedCqrsNotificationRegistry.g.cs` | The notification registry (which notifications are handled, which have stable names). |
| `CqrsGeneratedOutboxNotificationSerializer.g.cs` | The AOT-safe (de)serializer for `[NotificationName]` notifications (emitted only when there are any). |
| `CqrsGeneratedAssemblyMarkers.g.cs` | Assembly-level marker attributes for cross-assembly handler/notification discovery. |

A second generator, `CqrsAotHintGenerator`, emits an `AotHintProvider` that keeps generic instantiations
rooted for the AOT compiler. The dispatch/registry types are emitted into `CQRSharp.Core.*` (e.g.
`CQRSharp.Core.Extensions` for the registrar) and `*.Generated` namespaces; `AddCqrsGenerated` lands in
`CQRSharp.Core.Extensions`.

## The well-known-type handoff

The generator must recognize framework types (`ICommand`, `IQuery<>`, `INotificationHandler<>`, …)
without hardcoding their fully-qualified names. It does this with a **typeof-based metadata handoff**
rather than FQN string lookups:

- `CQRSharp.Core` marks each framework type with an assembly-level
  `[CqrsWellKnownType(CqrsRole.X, typeof(TheType))]` attribute. `CqrsRole` is an enum naming each role
  (command, query handler, pipeline behavior, …).
- At generation time, `CqrsKnownSymbols` scans those assembly attributes across the compilation and its
  references and builds a `CqrsRole → INamedTypeSymbol` map. Matching is done with Roslyn symbol
  comparison, so it is **namespace-agnostic and version-tolerant**: the generator finds the types by
  identity, not by name.
- The only fixed string is the well-known-type attribute's own metadata name. If a role can't be
  resolved, the generator reports **CQRGEN007** rather than silently emitting an empty registry; if
  `CQRSharp.Core` isn't referenced at all, it reports **CQRGEN008** and skips generation.

This is why the framework can move its types around internally without breaking generated output — the
handoff is by symbol, not by string.

## The incremental pipeline

Both generators are `IIncrementalGenerator`s built on an **equatable-records pipeline**. The per-syntax
transform projects each candidate type into a small, value-equatable model (`CandidateModel`), and
**no Roslyn symbols or `Compilation` flow past that point**. Two consequences:

- **Caching.** An edit that doesn't change a type's CQRSharp-relevant shape yields an identical model, so
  the downstream output step short-circuits and nothing is re-emitted.
- **No retention.** Because nothing symbol-bearing is held across generations, the generator doesn't pin
  large compilation objects in memory between runs.

Compilation-level facts the per-type transform can't see (is `CQRSharp.Core` referenced? is the
Pipelines builder present?) are resolved once into a separate equatable snapshot, so that branch only
re-runs when those facts actually change.

## The outbox serializer

The outbox serializer is **hand-rolled** over `Utf8JsonReader`/`Utf8JsonWriter` — one
`Serialize_`/`Deserialize_` pair per notification, plus per-type object and collection helpers, deduped
so a shared nested type emits once. System.Text.Json's source generator **cannot** be used: Roslyn runs
every generator against the user's original syntax, so a `[JsonSerializable]` context CQRSharp emits is
invisible to STJ's generator, and reflection-based `JsonSerializer` is not AOT-safe. The hand-rolled
emitter is the only fully-automatic, AOT-safe path. Notifications whose shape it can't handle are
reported as **CQRGEN005**.

## Cross-assembly discovery

Handlers and notifications often live in different assemblies from where they're dispatched. The
`CqrsGeneratedAssemblyMarkers.g.cs` file emits one assembly-level attribute per handled request and
notification, so the **analyzers** (`CQRA003` "no handler", `CQRA006` "no subscriber") can see handlers
in referenced assemblies, not just the current compilation. The runtime registrations themselves are
per-assembly: each assembly's `AddCqrsGenerated` registers its own handlers, and you call the one for the
assembly that owns the wiring (typically your host).

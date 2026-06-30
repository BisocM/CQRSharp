# The source generator

Everything CQRSharp does at runtime is wired up at **compile time** by two Roslyn generators. This page
explains what they emit and how they stay AOT-safe and incremental. You don't need any of this to *use*
CQRSharp — it's here for the curious and for contributors.

- [What gets generated](#what-gets-generated)
- [The well-known-type handoff](#the-well-known-type-handoff)
- [The incremental pipeline](#the-incremental-pipeline)
- [The outbox serializer](#the-outbox-serializer)
- [Multi-assembly applications](#multi-assembly-applications)

## What gets generated

`CqrsSourceGenerator` scans each compilation for requests, handlers, contexts, and notifications. Because
CQRSharp supports apps spread across several assemblies (see
[Multi-assembly applications](#multi-assembly-applications)), the output splits into two layers: a **module**
that every assembly with handlers emits, and a **bootstrap** that only the composition root emits.

| Generated source | Emitted in | Contains |
| --- | --- | --- |
| `CqrsModule.g.cs` | every assembly with handlers | This assembly's `ICqrsModule` — its registration data (handlers, request metadata, context factories, exception hooks) and AOT-safe dispatch factories — plus a `CqrsModuleRegistrar` that registers it. |
| `GeneratedRequestDispatcher.g.cs` | every assembly with handlers | This assembly's `Send` dispatch table — a typed lookup from request to its handler pipeline. |
| `GeneratedStreamRequestDispatcher.g.cs` | every assembly with handlers | The same for `Stream`. |
| `GeneratedDirectNotificationDispatcher.g.cs` | every assembly with handlers | This assembly's typed notification fan-out used by `Publish`. |
| `GeneratedCqrsDiagnostics.g.cs` | every assembly with handlers | The `ICqrsDiagnostics` describer for this assembly's request bindings. |
| `GeneratedOutboxNotificationSerializer.g.cs` | when there are `[NotificationName]` notifications | The AOT-safe (de)serializer for this assembly's stable notifications. |
| `CqrsGeneratedAssemblyMarkers.g.cs` | every assembly with handlers | Assembly-level markers: one per handled request/notification (for the analyzers) and one `[CqrsGeneratedModule]` naming this assembly's registrar. |
| `CqrsGeneratedBootstrap.g.cs` | the composition root only | `AddCqrsGenerated(...)` / `AddGenerated()` — registers every module in the reference graph (this assembly's and each referenced assembly's), then `AddCqrsModuleComposition()` merges them. |

The module's types are emitted into a per-assembly namespace (`CQRSharp.Generated.<AssemblyName>`) so two assemblies
in one reference graph never collide. The composition-root entry points stay in the canonical `CQRSharp.Core.Extensions`,
so consumer code always calls a stable `AddCqrsGenerated(...)`. A second generator, `CqrsAotHintGenerator`, emits an
`AotHintProvider` that keeps generic instantiations rooted for the AOT compiler.

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

## Multi-assembly applications

CQRSharp can run across several assemblies in one reference graph — handlers in `Application`, more in
`Infrastructure`, a host that ties them together. Two mechanisms make that work without the generated code
colliding:

- **Per-assembly modules.** Every assembly with handlers emits its own module (above) into a unique namespace
  and registers it additively as an `ICqrsModule`. Crucially, a module registers *its own* handlers — including
  `internal` ones a different assembly could never name — so encapsulation is preserved.
- **One composition root.** Exactly one assembly emits the global `AddCqrsGenerated()` / `AddGenerated()`. It
  reads the `[CqrsGeneratedModule]` marker off each referenced assembly at compile time, calls every module's
  registrar, then `AddCqrsModuleComposition()` merges the modules into one set of registries and a routing
  dispatcher per kind (request, stream, notification) that dispatches by runtime type to the owning module —
  all at compile time, no runtime reflection.

The composition root is your **executable** by default. If your entry point is a library — a plugin host, a test
host — mark it:

```xml
<PropertyGroup>
  <CQRSharpCompositionRoot>true</CQRSharpCompositionRoot>
</PropertyGroup>
```

The analyzers also use the per-request/notification markers for cross-assembly checks (`CQRA003` "no handler",
`CQRA006` "no subscriber"), so a request whose handler lives in a referenced assembly isn't falsely flagged.

> **Accessibility.** Within an assembly the generator registers `public` and `internal` handlers. A handler less
> accessible than `internal` (e.g. a `private` nested type) can't be registered — make it at least `internal`.

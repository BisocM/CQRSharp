# The source generator

CQRSharp's dispatch and registration are written at **compile time** by a Roslyn incremental generator,
`CqrsSourceGenerator`, which ships in the `CQRSharp` meta-package. This page explains what it emits, how it recognizes
CQRSharp's types, how it stays incremental, and how it composes applications made of several assemblies. You do not need
it to *use* CQRSharp; it is here for the curious, for library authors and for contributors.

- [What gets generated](#what-gets-generated)
- [Recognizing framework types](#recognizing-framework-types)
- [The incremental pipeline](#the-incremental-pipeline)
- [The outbox serializer and the fingerprinter](#the-outbox-serializer-and-the-fingerprinter)
- [Multi-assembly applications](#multi-assembly-applications)
- [What the generator registers](#what-the-generator-registers)

## What gets generated

The generator runs in every project that references the `CQRSharp` package, which also brings `CQRSharp.Core`; without
`CQRSharp.Core` it generates nothing. Its output has two layers: a **module** in each assembly that has something to
register, and the **entry points** (`AddCqrsGenerated`) in every assembly.

| Generated source | Emitted in | Contains |
| --- | --- | --- |
| `CqrsModule.g.cs` | every assembly with requests, handlers, notifications, context factories, validators, exception hooks or closed behaviors | An internal `CqrsModule : ICqrsModule` with the assembly's tables: request metadata, typed handler invokers, request and stream routes, context sources, exception hooks, notification routes and subscriptions (one per handler, with its stable name), and `PartitionBy` key selectors. A public, IntelliSense-hidden `CqrsModuleRegistrar` registers the handlers and the other discovered services, the closed behaviors used under Native AOT, and the module. |
| `GeneratedOutboxNotificationSerializer.g.cs` | when the assembly declares `[NotificationName]` notifications the serializer can handle | The AOT-safe `INotificationSerializer` for them. |
| `GeneratedRequestFingerprinter.g.cs` | when the assembly declares or handles `IIdempotentRequest`s it can fingerprint | The SHA-256 payload fingerprint of each such request, which the idempotency behaviors store with the key so a key reused with a different payload is rejected. |
| `CqrsGeneratedBootstrap.g.cs` | every assembly | The `internal` `AddCqrsGenerated()` and `AddCqrsGenerated(Action<ICqrsBuilder>)` entry points: they register CQRSharp's services, every module in the assembly's reference graph, and the composition that merges them. |
| `CqrsGeneratedAssemblyMarkers.g.cs` | every assembly | Assembly-level attributes: `[CqrsGeneratedModule]` naming the module's registrar, and one `[CqrsHandledRequest]`, `[CqrsHandledNotification]` or `[CqrsRegisteredContextFactory]` per handled request, handled notification or context type with a factory, which the analyzers read across assemblies. |

A module's types live in a namespace of their own: `CQRSharp.Generated.` followed by the assembly name, one namespace
segment per dotted part (`Orders.Api` becomes `CQRSharp.Generated.Orders.Api`). A part that is not a C# identifier as it
stands is made one (an invalid character becomes `_`, a leading digit or a keyword gets a `_`), and the last segment then
ends in `_` and an eight-digit hexadecimal hash of the exact assembly name (`Orders-Api` becomes
`CQRSharp.Generated.Orders_Api_` followed by the hash), so two assemblies never share a module namespace. Only the
registrar is public, and hidden, so a library's generated module is not part of its public API.

The entry points are emitted into the `CQRSharp` namespace as the `internal` class `CqrsGeneratedBootstrap`. Because they
are internal, the copies two assemblies emit never collide: each assembly calls its own. The builder form, emitted where
the project references `CQRSharp.Pipelines`, applies the builder first and the generated registrations after it; there
the parameterless form is the builder form with an empty configuration, so it registers the validation and
exception-handling behaviors too. Without `CQRSharp.Pipelines` only the parameterless form exists, and it registers no
pipeline behavior.

## Recognizing framework types

The generator and the analyzers recognize CQRSharp's types (`ICommand`, `IQueryHandler<,>`,
`INotificationPipelineBehavior<>`, ...) by metadata name, from one internal table that a test resolves against the real
assemblies. When a required type cannot be resolved although `CQRSharp.Core` is referenced, the generator reports
**CQRGEN007** and generates nothing, instead of emitting code that does not compile; referencing the same version of every
CQRSharp package fixes it.

Generated code names public, API-tracked runtime types, so those names are a binary contract between compiled modules and
the runtime within the 5.x line. A library's module must be compiled with a CQRSharp 5 generator to run on a CQRSharp 5
runtime.

## The incremental pipeline

The generator is an `IIncrementalGenerator`. Its steps:

- **Candidates.** For every class, struct and record in the compilation, on every edit, a semantic transform projects
  the type into a small value-equatable model. No Roslyn symbol or `Compilation` flows past this step, so the generator
  never holds on to compilation objects between runs.
- **Compilation facts.** Facts the per-type transform cannot see (is `CQRSharp.Core` referenced, is the builder present,
  which modules and behaviors do the referenced assemblies bring) are gathered into a separate equatable snapshot,
  recomputed for each compilation.
- **Code generation** reads the models without their source locations, together with the snapshot. When those compare
  equal to the previous run's, nothing is re-emitted: an edit that only moves code (a line added above a type) or changes
  a method body leaves the generated code as it is.
- **Diagnostics** read the models with their locations, so they are reported where the code is.

## The outbox serializer and the fingerprinter

The outbox serializer is written directly over `Utf8JsonReader` and `Utf8JsonWriter`: one serialize and deserialize pair
per notification, plus helpers for nested objects and collections. System.Text.Json's own source generator cannot be used,
because Roslyn runs every generator on the user's original syntax, so a `[JsonSerializable]` context CQRSharp emitted
would be invisible to it, and reflection-based `JsonSerializer` is not AOT-safe. The shapes the serializer supports, and
how a notification's type can change while messages are stored, are listed in [The outbox](outbox.md). A
`[NotificationName]` notification whose shape it cannot handle is reported as **CQRGEN005** (an error).

The request fingerprinter renders an idempotent request's properties the same way and hashes the result. An idempotent
request it cannot render gets no automatic fingerprint and is reported as **CQRGEN014**; implement `IFingerprintedRequest`
to supply one, or leave non-payload properties out with an unconditional `[JsonIgnore]`
([Idempotency and resilience](idempotency-and-resilience.md)).

## Multi-assembly applications

CQRSharp runs across several assemblies in one reference graph (handlers in `Application`, more in `Infrastructure`, a
host that ties them together) with no setup beyond referencing the package:

- **A module per assembly.** Each assembly registers its own handlers, `internal` ones included, which another assembly
  could not name.
- **The entry point composes the graph.** An assembly's `AddCqrsGenerated()` reads the `[CqrsGeneratedModule]` marker of
  each referenced assembly at compile time, registers every referenced module and then its own, and merges them into one
  set of registries and routes. There is no runtime assembly scanning.

**There is no composition root to designate.** Call `AddCqrsGenerated()` from wherever you set up DI (usually the host's
`Program.cs`), and it wires that assembly's whole reference graph. The calling assembly's module registers last and the
composition is last-wins, so a request that both a library and the host handle is served by the host. A test project that
references the host has its own internal entry point and uses it.

**`InternalsVisibleTo` is the exception.** When a referenced assembly exposes its internals to the calling one, its
`AddCqrsGenerated()` is visible there too. The generator then also emits the calling assembly's entry point in its module
namespace, and reports **CQRGEN015** at each plain `AddCqrsGenerated()` call, naming the unambiguous form:
`CQRSharp.Generated.<AssemblyName>.CqrsGeneratedBootstrap.AddCqrsGenerated(services)` (or a `using` for that namespace
inside a namespace block). An assembly with no module of its own and exactly one such visible entry point keeps the plain
call, which binds to that entry point and registers the same graph.

**The one rule: reference the `CQRSharp` package from every project that declares handlers** (so it emits a module) and
from the project that calls `AddCqrsGenerated()` (so the entry point exists there). A project that declares handlers
without the generator gets **CQRA010**: the analyzers ship in `CQRSharp.Abstractions` and reach every project that
references any CQRSharp package, so they warn even where the generator does not run. The analyzers also read the handled
request and notification markers, so **CQRA003** ("no handler") and **CQRA006** ("no subscriber") do not fire for a
request or notification handled in a referenced assembly.

A contracts project that declares requests whose handlers live elsewhere can silence **CQRGEN003** ("no handler found")
with `<NoWarn>$(NoWarn);CQRGEN003</NoWarn>` or `dotnet_diagnostic.CQRGEN003.severity = none`.

## What the generator registers

The generator registers closed, non-abstract types that are `public` or `internal`: command, query, stream and
notification handlers, context factories, validators, exception hooks, and closed pipeline behaviors. Generated code must be able to
name everything it registers or routes, so it reports what it has to leave out:

- a handler less accessible than `internal` (a `private` nested class, say): **CQRGEN006**;
- an open-generic handler (`Handler<T>`): **CQRGEN009**;
- a request, result, item or bound type generated code cannot name (less accessible than `internal`, or internal to
  another assembly without `InternalsVisibleTo`): **CQRGEN010**; the request is not routed, or the validator, hook or
  behavior bound over the type never runs;
- a request attribute (an interceptor or a `[PipelineExemption]`) generated code cannot rebuild: **CQRGEN016**, an error.

[Diagnostics](diagnostics.md) lists every `CQRGEN` code. Under Native AOT the generator also closes open-generic
behaviors over value-type results and notifications; see [Native AOT](native-aot.md#value-type-results-and-notifications).

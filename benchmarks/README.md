# Benchmarks

Dispatch overhead of CQRSharp against [MediatR](https://github.com/LuckyPennySoftware/MediatR) and
[Mediator](https://github.com/martinothamar/Mediator), measured two ways: per dispatch with the mediator already
resolved, and per request scope with one dispatch in it.

```bash
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter '*' --launchCount 3   # everything, as published (~50 minutes)
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks                                     # everything, one launch each (~15 minutes)
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter '*NewScope*' --job short    # one of the two ways, quickly
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter '*Request_*' --job short   # one scenario, both ways
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --list flat                          # every benchmark's name
```

Without arguments the project runs every benchmark; with any, it passes them to BenchmarkDotNet. The times are rough and
depend on the machine. The project is deliberately **not** in `CQRSharp.sln`: it references third-party mediators the
library must never depend on, and it is run on demand rather than in CI. Publish numbers only from a quiet machine.

## Method

Two benchmark classes measure the same five scenarios:

- **`InScopeDispatchBenchmarks` — per dispatch, scope reused.** Each library's mediator is resolved once, from a DI scope
  kept for the whole run, and every operation dispatches through it. This isolates the dispatch itself: what a worker,
  or a handler that dispatches many times from one scope, pays per call.
- **`NewScopeDispatchBenchmarks` — per request scope, one dispatch.** Every operation creates a DI scope, resolves the
  mediator from it, dispatches once and disposes the scope. Everything a library builds per scope is in these numbers:
  this is what a web request pays.

The scenarios:

- **Request** — a query with a trivial handler.
- **Request + 1 behavior** — the same, with one pass-through pipeline behavior registered.
- **Notification** — a notification with one handler, published under its own type.
- **Notification as INotification** — the same notification published under the notification interface, as a list of
  domain events is, so the library finds the handlers from the runtime type.
- **Stream (3 items)** — a streaming request enumerated to its end: the dispatch plus the per-item cost of whatever each
  library wraps around the handler's `IAsyncEnumerable`.

Common to all of them:

- Handlers do nothing (`Task.FromResult(42)`), so the measurement is the framework's own cost.
- A **new request object per call** for every library. CQRSharp stamps a context onto the instance, so reusing one would
  skip that work and flatter it.
- Every library runs in **its documented default configuration**, and the lifetimes differ by design:
  - **CQRSharp**: `ICqrsDispatcher` scoped, handlers transient, the behavior registered as a transient open generic.
  - **MediatR 12.5**: `IMediator` and handlers transient, the behavior added with `AddOpenBehavior` (transient).
  - **Mediator 3.0**: its default Singleton lifetime (`IMediator` and handlers are singletons, which its README
    recommends for performance), the behavior registered with `AddSingleton`. A singleton handler cannot take a scoped
    dependency such as a `DbContext`; Mediator's Scoped and Transient lifetimes, which can, cost more and are not
    measured here. Its compile-time `PipelineBehaviors` option is not used: it would add the behavior to the
    configuration without one as well.
- **MediatR is pinned to 12.5.0**, the last release under Apache-2.0. 13.0 and later are commercially licensed; check
  their terms before benchmarking or shipping a newer version.

This is dispatch overhead, not a feature comparison. A CQRSharp request also creates a request context; its lifecycle
notifications (`Initiated` / `Completed` / `Failed`), tracing and metrics are pay-for-use and nothing subscribes to them
in these benchmarks.

## Latest results

BenchmarkDotNet prints one table per class, grouped by scenario, with each library's time and allocation as a ratio of
MediatR's. No results have been published for this harness yet: publish both tables here from a single full run (the
first command above), with the environment line BenchmarkDotNet prints, and rewrite any summary elsewhere from them.

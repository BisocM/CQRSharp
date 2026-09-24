# Benchmarks

Dispatch overhead of CQRSharp against [MediatR](https://github.com/LuckyPennySoftware/MediatR), measured two ways: per
dispatch with the mediator already resolved, and per request scope with one dispatch in it.

```bash
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter '*' --launchCount 3   # everything, as published (~50 minutes)
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks                                     # everything, one launch each (~15 minutes)
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter '*NewScope*' --job short    # one of the two ways, quickly
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter '*Request_*' --job short   # one scenario, both ways
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --list flat                          # every benchmark's name
```

Without arguments the project runs every benchmark; with any, it passes them to BenchmarkDotNet. The times are rough and
depend on the machine. The project is deliberately **not** in `CQRSharp.sln`: it references a third-party mediator the
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
- **MediatR is pinned to 12.5.0**, the last release under Apache-2.0. 13.0 and later are commercially licensed; check
  their terms before benchmarking or shipping a newer version.

This is dispatch overhead, not a feature comparison. A CQRSharp request also creates a request context; its lifecycle
notifications (`Initiated` / `Completed` / `Failed`), tracing and metrics are pay-for-use and nothing subscribes to them
in these benchmarks.

## Latest results

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS; 13th Gen Intel Core i9-13900K; .NET 8.0.31 (SDK 10.0.400); three
launches per benchmark. Compare the ratios: they come from one run, while absolute times depend on the machine.

### In a long-lived scope (the dispatcher resolved once)

| Scenario | Library | Mean | vs MediatR | Allocated |
| --- | --- | ---: | ---: | ---: |
| Request | CQRSharp | 57.750 ns | 0.71× | 144 B |
| Request | MediatR 12.5 | 81.739 ns | 1.00× | 336 B |
| Request + 1 behavior | CQRSharp | 97.140 ns | 0.86× | 320 B |
| Request + 1 behavior | MediatR 12.5 | 113.572 ns | 1.00× | 528 B |
| Notification | CQRSharp | 104.092 ns | 1.28× | 72 B |
| Notification | MediatR 12.5 | 81.537 ns | 1.00× | 312 B |
| Notification as INotification | CQRSharp | 94.365 ns | 1.21× | 72 B |
| Notification as INotification | MediatR 12.5 | 77.873 ns | 1.00× | 312 B |
| Stream (3 items) | CQRSharp | 110.912 ns | 0.52× | 168 B |
| Stream (3 items) | MediatR 12.5 | 212.573 ns | 1.00× | 560 B |

### Per request (new DI scope, resolve, dispatch, dispose)

| Scenario | Library | Mean | vs MediatR | Allocated |
| --- | --- | ---: | ---: | ---: |
| Request | CQRSharp | 179.43 ns | 1.32× | 728 B |
| Request | MediatR 12.5 | 135.75 ns | 1.00× | 568 B |
| Request + 1 behavior | CQRSharp | 225.89 ns | 1.28× | 904 B |
| Request + 1 behavior | MediatR 12.5 | 176.47 ns | 1.00× | 760 B |
| Notification | CQRSharp | 299.89 ns | 2.33× | 520 B |
| Notification | MediatR 12.5 | 128.61 ns | 1.00× | 472 B |
| Notification as INotification | CQRSharp | 280.84 ns | 2.20× | 520 B |
| Notification as INotification | MediatR 12.5 | 127.86 ns | 1.00× | 472 B |
| Stream (3 items) | CQRSharp | 225.08 ns | 0.85× | 680 B |
| Stream (3 items) | MediatR 12.5 | 265.16 ns | 1.00× | 720 B |

How to read it: resolved once, CQRSharp dispatches a request, a request with a behavior and a stream faster than MediatR
and allocates less in every scenario; its notification publish is about a quarter slower. Per request, where a fresh
scope and the scoped `ICqrsDispatcher` are part of the cost, CQRSharp is slower than MediatR except for streams, most of
all for notifications.

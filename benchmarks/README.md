# Benchmarks

Per-dispatch overhead of CQRSharp against [MediatR](https://github.com/LuckyPennySoftware/MediatR) and
[Mediator](https://github.com/martinothamar/Mediator).

```bash
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks             # everything (~8 minutes)
dotnet run -c Release --project benchmarks/CQRSharp.Benchmarks -- --filter "*Request_*" --job short
```

The project is deliberately **not** in `CQRSharp.sln`: it references third-party mediators the library must never depend
on, and it is run on demand rather than in CI.

## Method

- Handlers do nothing (`Task.FromResult(42)`), so the measurement is the framework's own cost.
- Every library dispatches from one long-lived DI scope, as a request scope would in a real host.
- A **new request object per call** for every library. CQRSharp stamps metadata and a context onto the instance, so
  reusing one would skip that work and flatter it.
- "+ 1 behavior" adds a single pass-through pipeline behavior, registered the way each library documents.
- "Request in a new DI scope" creates a scope, resolves the dispatcher from it, dispatches once and disposes the
  scope — what a web request pays. Mediator is configured with a scoped lifetime for this comparison; MediatR's
  `IMediator` is transient by default; CQRSharp's `ICqrsDispatcher` is scoped by design.
- **MediatR is pinned to 12.5.0**, the last release under Apache-2.0. 13.0 and later are commercially licensed; check
  their terms before benchmarking or shipping a newer version.

This is dispatch overhead, not a feature comparison. A CQRSharp request also creates a request context; its lifecycle
notifications (`Initiated` / `Completed` / `Failed`) are pay-for-use and are not subscribed to in these benchmarks.

## Latest results

BenchmarkDotNet v0.15.8, Windows 11 (10.0.22631.5039/23H2/2023Update/SunValley3); AMD Ryzen 9 7950X3D 4.20GHz; .NET 8.0.26

| Scenario | Library | Mean | vs MediatR | Allocated |
| --- | --- | ---: | ---: | ---: |
| Request | CQRSharp | 74.24 ns | 1.02× | 152 B |
| Request | MediatR 12.5 | 72.69 ns | 1.00× | 336 B |
| Request | Mediator 3.0 (source-gen) | 57.01 ns | 0.78× | 88 B |
| Request + 1 behavior | CQRSharp | 112.87 ns | 1.09× | 328 B |
| Request + 1 behavior | MediatR 12.5 | 103.25 ns | 1.00× | 528 B |
| Request + 1 behavior | Mediator 3.0 (source-gen) | 77.58 ns | 0.75× | 184 B |
| Notification | CQRSharp | 60.52 ns | 0.79× | 80 B |
| Notification | MediatR 12.5 | 76.33 ns | 1.00× | 312 B |
| Notification | Mediator 3.0 (source-gen) | 41.78 ns | 0.55× | 24 B |
| Request in a new DI scope | CQRSharp | 255.45 ns | 1.87× | 720 B |
| Request in a new DI scope | MediatR 12.5 | 136.93 ns | 1.00× | 568 B |
| Request in a new DI scope | Mediator 3.0 (source-gen) | 169.52 ns | 1.24× | 536 B |

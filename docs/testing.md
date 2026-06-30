# Testing

CQRSharp is built to be testable: handlers are plain classes, time flows through an injectable
`TimeProvider`, the wiring is introspectable, and the store contracts come with a ready-made conformance
suite.

- [Testing handlers](#testing-handlers)
- [Testing through the dispatcher](#testing-through-the-dispatcher)
- [Controlling time](#controlling-time)
- [Asserting wiring](#asserting-wiring)
- [Testing notifications](#testing-notifications)
- [Store contract tests](#store-contract-tests)

## Testing handlers

A handler is an ordinary class with no framework coupling, so the simplest test constructs it directly:

```csharp
[Fact]
public async Task CreateUser_succeeds()
{
    var handler = new CreateUserHandler(/* fakes */);
    var result = await handler.Handle(new CreateUser { Name = "Ada" }, CancellationToken.None);
    result.IsSuccess.Should().BeTrue();
}
```

This tests your logic in isolation — no DI, no pipeline. Use it for the bulk of your handler tests.

## Testing through the dispatcher

To exercise the **full pipeline** (validation, behaviors, lifecycle notifications), build a host with
`AddCqrsGenerated` and resolve `ICqrsDispatcher`:

```csharp
using var host = new HostBuilder()
    .ConfigureServices(s => s.AddCqrsGenerated(b => b.UseValidation()))
    .Build();
await host.StartAsync();

using var scope = host.Services.CreateScope();
var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

var result = await cqrs.Send(new CreateUser { Name = "" });   // exercises validation, etc.
```

Starting the host also runs the **startup validator**, so a misconfigured test setup fails fast with a
`CQRCONF` message.

## Controlling time

Every time-dependent component (timeouts, retry back-off, rate-limiter refill, outbox timestamps, logging
durations) reads the clock through an injected `TimeProvider`. In tests, register a `FakeTimeProvider`
(from `Microsoft.Extensions.Time.Testing`) via `UseTimeProvider` and advance it to drive time-dependent
behavior **without real delays**:

```csharp
var clock = new FakeTimeProvider();
services.AddCqrsGenerated(b => b
    .UseResilience(o => o.BaseDelay = TimeSpan.FromSeconds(5))
    .UseTimeProvider(clock));

// ... dispatch a failing retryable request ...
clock.Advance(TimeSpan.FromSeconds(5));   // fast-forward the back-off
```

This makes retry/timeout/rate-limit tests deterministic and instant.

## Asserting wiring

The [diagnostics introspection API](diagnostics.md#the-introspection-api) lets a test assert exactly how a
request is bound — which handler, which behaviors, which exemptions:

```csharp
var diag = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
var binding = diag.DescribeRequest(typeof(Heartbeat));

binding.HandlerType.Should().Be(typeof(HeartbeatHandler));
binding.ExemptedPipeline.Should().Contain(b => b.BehaviorType.Name.StartsWith("Logging"));
```

`DescribeConfiguration()` returns the global `CQRCONF` issues, so you can assert your wiring is clean (or
that a deliberate misconfiguration is detected).

## Testing notifications

For notification fan-out and publish strategies, you can drive `DirectNotificationDispatcher` directly
with a hand-built `ServiceCollection`, configuring `NotificationOptions`:

```csharp
var services = new ServiceCollection();
services.Configure<NotificationOptions>(o => o.PublishStrategy = PublishStrategy.ParallelWhenAllAggregate);
services.AddSingleton<INotificationHandler<OrderPlaced>, FirstHandler>();
services.AddSingleton<INotificationHandler<OrderPlaced>, SecondHandler>();
using var provider = services.BuildServiceProvider();

await new DirectNotificationDispatcher(provider).Publish(new OrderPlaced(id, total), default);
```

This is how the framework's own publish-strategy and fault-isolation tests are written.

## Store contract tests

If you implement a custom `IOutboxStore` or `IIdempotencyStore`, verify it against the **store contract
tests** rather than re-deriving the rules. The conformance suite (`OutboxStoreContractTests`,
`IdempotencyStoreContractTests`) is an abstract base you derive from, supplying your store:

```csharp
public sealed class MyOutboxStoreContractTests : OutboxStoreContractTests
{
    protected override IOutboxStore CreateStore() => new MyOutboxStore(/* ... */);
}
```

The contract covers atomic claiming (`Pending → InProgress`), no double-claim under concurrency,
persisted retry/back-off, dead-lettering, FIFO ordering, and (for idempotency) atomic claim/release and
case-sensitive keys. The built-in in-memory, Redis, and EF Core stores all pass the same suite, so a
custom store that passes it behaves identically. This is the same harness the framework uses for its own
stores.

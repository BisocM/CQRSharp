# Testing

CQRSharp is built to be testable: handlers are plain classes, time flows through an injectable `TimeProvider`, the
wiring is introspectable, and two packages ship test support: a fake dispatcher and the store contract suites.

- [Testing handlers](#testing-handlers)
- [Testing through the dispatcher](#testing-through-the-dispatcher)
- [Controlling time](#controlling-time)
- [Asserting wiring](#asserting-wiring)
- [Testing notifications](#testing-notifications)
- [Testing code that calls the dispatcher](#testing-code-that-calls-the-dispatcher)
- [Store contract tests](#store-contract-tests)

## Testing handlers

A handler is an ordinary class with no framework coupling, so the simplest test constructs it directly:

```csharp
[Fact]
public async Task CreateUser_succeeds()
{
    var handler = new CreateUserHandler(/* fakes */);
    var result = await handler.Handle(new CreateUser { Name = "Ada" }, CancellationToken.None);
    Assert.True(result.IsSuccess);
}
```

This tests your logic in isolation: no DI, no pipeline. Use it for the bulk of your handler tests.

The dispatcher sets a request's `Context` from its context factory. A handler that reads `request.Context` needs one in a
direct test; `RequestBase<TContext>.Context` has no public setter, so set it through the `IRequest` interface:

```csharp
var command = new CreateUser { Name = "Ada" };
((IRequest)command).Context = new ShopContext { UserId = "u-1" };   // the context type CreateUser declares
```

The setter rejects a context of another type with `ArgumentException`.

## Testing through the dispatcher

To exercise the **full pipeline** (validation, behaviors, lifecycle notifications), build a host with
`AddCqrsGenerated` and resolve `ICqrsDispatcher` from a scope. The test project must reference the CQRSharp package, so
the source generator registers the handlers it declares or references:

```csharp
using var host = new HostBuilder()
    .ConfigureServices(s => s.AddCqrsGenerated(b => b.ValidateOnStart()))
    .Build();
await host.StartAsync();

using var scope = host.Services.CreateScope();
var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

var result = await cqrs.Send(new CreateUser { Name = "" });   // runs validation and every behavior
```

`AddCqrsGenerated(b => ...)` registers the validation and exception-handling behaviors; the other behaviors are added by
their verbs. `ValidateOnStart()` makes starting the host run the **startup validator**, so a misconfigured test setup
fails fast with a `CQRCONF` message (see [Diagnostics](diagnostics.md#startup-validation-cqrconf)).

## Controlling time

Every time-dependent component (timeouts, retry back-off, rate-limiter refill, outbox timestamps and polling, logging
durations) reads the clock through an injected `TimeProvider`. In tests, register a `FakeTimeProvider` (from
`Microsoft.Extensions.TimeProvider.Testing`) with `UseTimeProvider` and advance it to drive time-dependent behavior
**without real delays**:

```csharp
var clock = new FakeTimeProvider();
services.AddCqrsGenerated(b => b
    .UseResilience(o => o.BaseDelay = TimeSpan.FromSeconds(5))
    .UseTimeProvider(clock));

// ... dispatch a failing retryable request ...
clock.Advance(TimeSpan.FromSeconds(5));   // fast-forward the back-off
```

This makes retry, timeout and rate-limit tests deterministic and instant.

## Asserting wiring

The [diagnostics introspection API](diagnostics.md#the-introspection-api) lets a test assert exactly how a request is
bound: which handler, which behaviors, which exemptions:

```csharp
using CQRSharp.Core.Diagnostics;

var diag = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>();
var binding = diag.DescribeRequest(typeof(Heartbeat));

Assert.Equal(typeof(HeartbeatHandler), binding.HandlerType);
Assert.Contains(binding.ExemptedPipeline, b => b.BehaviorType.Name.StartsWith("Logging"));
Assert.Empty(diag.DescribeConfiguration());   // no CQRCONF issue
```

## Testing notifications

A notification handler is a plain class too, so test it directly by calling its `Handle` method. To test the fan-out,
publish through the real dispatcher of a host built as [above](#testing-through-the-dispatcher) and assert on what the
handlers did. `ConfigureNotifications` picks the publish strategy:

```csharp
services.AddCqrsGenerated(b => b.ConfigureNotifications(o => o.PublishStrategy = PublishStrategy.ParallelWhenAllAggregate));

// ...
await cqrs.Publish(new OrderPlaced(orderId, total));
```

With the outbox on, a durable notification is stored rather than handled at once; leave the outbox off in tests of
in-process fan-out.

## Testing code that calls the dispatcher

Code that *calls* the dispatcher (an endpoint, a controller, an application service) can be unit-tested without a
container or a pipeline: hand it `RecordingCqrsDispatcher` from the `CQRSharp.Testing` package, stub what the queries
return, and assert on what was sent and published. See
[The testing packages](testing-package.md#unit-testing-a-consumer-of-icqrsdispatcher).

## Store contract tests

A custom `IOutboxStore`, `IInboxStore` or `IIdempotencyStore` is verified by deriving from the abstract xUnit v3 suites
in the `CQRSharp.Testing.Xunit.V3` package, the same suites the built-in stores pass. See
[The testing packages](testing-package.md#contract-testing-an-outbox-store).

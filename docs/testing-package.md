# The CQRSharp.Testing package

`CQRSharp.Testing` is the test-support package. It ships two things:

- **Store contract suites** - the abstract xUnit suites the built-in in-memory, EF Core, and Redis stores pass.
  Derive from them to prove a custom `IOutboxStore` or `IIdempotencyStore` has the same semantics.
- **`RecordingCqrsDispatcher`** - a fake `ICqrsDispatcher` for unit-testing code that *calls* the dispatcher
  (controllers, endpoints, application services) without a container or a pipeline.

For testing handlers, behaviors, and the real dispatcher, see [Testing](testing.md).

```
dotnet add package CQRSharp.Testing
```

Everything lives in the `CQRSharp.Testing` namespace.

## Contents

- [Dependencies](#dependencies)
- [Contract-testing an outbox store](#contract-testing-an-outbox-store)
- [Contract-testing an idempotency store](#contract-testing-an-idempotency-store)
- [Skipping when a backing service is absent](#skipping-when-a-backing-service-is-absent)
- [Unit-testing a consumer of ICqrsDispatcher](#unit-testing-a-consumer-of-icqrsdispatcher)
- [RecordingCqrsDispatcher reference](#recordingcqrsdispatcher-reference)

## Dependencies

The contract suites are written for **xUnit v2** (`xunit` 2.9.x), so the test project that derives from them must be
an xUnit v2 project with its usual runner packages (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`).
The package itself depends only on `xunit.assert`, `xunit.extensibility.core`, `xunit.extensibility.execution`,
`Xunit.SkippableFact`, and `Microsoft.Extensions.TimeProvider.Testing` (for `FakeTimeProvider`), plus
`CQRSharp.Abstractions` and `CQRSharp.Core`. It brings no assertion or mocking library; failures are reported through
plain `Xunit.Assert` with a message naming the violated contract rule.

`RecordingCqrsDispatcher` uses none of the xUnit types, so it works under any test framework.

## Contract-testing an outbox store

Derive a sealed class from `OutboxStoreContractTests` and supply two things: the `FakeTimeProvider` the store reads
its clock from, and a factory for a fresh, empty store bound to that clock. The suite drives all timing (back-off,
visibility timeouts, lease renewal) by advancing the fake clock, so it never sleeps; a store that reads
`DateTime.UtcNow` directly instead of the injected `TimeProvider` will fail it.

```csharp
using CQRSharp.Pipelines;
using CQRSharp.Testing;
using Microsoft.Extensions.Time.Testing;

public sealed class MyOutboxStoreTests : OutboxStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IOutboxStore> CreateStoreAsync()
        => Task.FromResult<IOutboxStore>(new MyOutboxStore(Time, visibilityTimeout: VisibilityTimeout));
}
```

xUnit creates a new instance of the class per test, so `Time` and the store start fresh every time.

`VisibilityTimeout` is a virtual property (default five minutes). Configure the store with it, as above, or override
it to match a timeout the store fixes itself:

```csharp
protected override TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(1);
```

The suite covers: claiming transitions a message to `InProgress` and issues an `OutboxClaim` (message id, token,
lease); FIFO order by `CreatedAt`; the batch-size limit; back-off via `NextRetryAt`; the visibility timeout and
reclaim of a crashed claimant's message; `MarkAsProcessedAsync` / `MarkAsFailedAsync` being terminal;
`IncrementAttemptAsync` counting, persisting the error, and rescheduling; stale claims being rejected by every
operation after a reclaim; `RenewAsync` extending the lease; `ReleaseAsync` returning a message without counting an
attempt; and atomic claiming under concurrent `GetPendingAsync` calls.

The single-instance suite cannot observe a double-claim between two *processes*. A store backed by a shared database
should add its own race test over two independent connections.

## Contract-testing an idempotency store

`IdempotencyStoreContractTests` needs only the store factory. Every test uses a unique key, so one shared backing
service can serve the whole run.

```csharp
using CQRSharp.Pipelines;
using CQRSharp.Testing;

public sealed class MyIdempotencyStoreTests : IdempotencyStoreContractTests
{
    protected override Task<IIdempotencyStore> CreateStoreAsync()
        => Task.FromResult<IIdempotencyStore>(new MyIdempotencyStore());
}
```

The suite covers: a fresh key is claimed; a second claim is rejected; an unfinished duplicate reports `InProgress`; a
completed key reports `Completed` and returns the stored result byte-for-byte (or none, when completed with `null`);
release frees an unfinished key but never a completed one; releasing or completing an unknown key is a no-op; and of
many concurrent claims of one key exactly one wins.

## Skipping when a backing service is absent

The suites' tests are `[SkippableFact]`s, so `CreateStoreAsync` may skip instead of fail when the store's backing
service is not reachable (a Redis or database server that only exists in CI, say):

```csharp
protected override async Task<IIdempotencyStore> CreateStoreAsync()
{
    Skip.IfNot(_fixture.Available, "No server is reachable.");
    return new MyIdempotencyStore(await _fixture.ConnectAsync());
}
```

`Skip` comes from `Xunit.SkippableFact` (namespace `Xunit`), which the package brings in.

## Unit-testing a consumer of ICqrsDispatcher

Given a service that depends on the dispatcher:

```csharp
public sealed class RenameUser(int id, string name) : CommandBase
{
    public int Id { get; } = id;
    public string Name { get; } = name;
}

public sealed class GetUserName(int id) : QueryBase<string>
{
    public int Id { get; } = id;
}

public sealed record UserRenamed(int Id) : INotification;

public sealed class UserService(ICqrsDispatcher dispatcher)
{
    public async Task<bool> RenameAsync(int id, string newName, CancellationToken cancellationToken = default)
    {
        var current = await dispatcher.Send(new GetUserName(id), cancellationToken);
        if (current == newName)
            return false;

        var result = await dispatcher.Send(new RenameUser(id, newName), cancellationToken);
        if (!result.IsSuccess)
            return false;

        await dispatcher.Publish(new UserRenamed(id), cancellationToken);
        return true;
    }
}
```

hand it a `RecordingCqrsDispatcher`, stub what the queries return, and assert on what was sent:

```csharp
using CQRSharp.Testing;

public sealed class UserServiceTests
{
    [Fact]
    public async Task Renaming_sends_the_command_and_publishes_the_event()
    {
        var dispatcher = new RecordingCqrsDispatcher()
            .Setup<GetUserName, string>(query => $"user-{query.Id}");
        var service = new UserService(dispatcher);

        var renamed = await service.RenameAsync(7, "Ada");

        Assert.True(renamed);
        var command = Assert.Single(dispatcher.Sent<RenameUser>());
        Assert.Equal("Ada", command.Name);
        Assert.Equal(7, Assert.Single(dispatcher.Published<UserRenamed>()).Id);
    }

    [Fact]
    public async Task A_rejected_rename_publishes_nothing()
    {
        var dispatcher = new RecordingCqrsDispatcher()
            .Setup<GetUserName, string>("Grace")
            .Setup<RenameUser, CommandResult>(CommandResult.FromError("Name is taken.", 409));
        var service = new UserService(dispatcher);

        Assert.False(await service.RenameAsync(7, "Ada"));
        Assert.Empty(dispatcher.PublishedNotifications);
    }

    [Fact]
    public async Task A_failing_query_propagates()
    {
        var dispatcher = new RecordingCqrsDispatcher()
            .Throws<GetUserName>(new TimeoutException());
        var service = new UserService(dispatcher);

        await Assert.ThrowsAsync<TimeoutException>(() => service.RenameAsync(7, "Ada"));
        Assert.Empty(dispatcher.Sent<RenameUser>());
    }
}
```

`RenameUser` needed no stub in the first test: an unstubbed plain `ICommand` succeeds. A value-returning request has
no sensible default, so sending one without a stub throws an `InvalidOperationException` that names the request type
- a forgotten stub fails at the `Send` call, not later as a `NullReferenceException` inside the code under test.

Streams are stubbed with `SetupStream`, from an in-memory sequence or a real `IAsyncEnumerable<T>`:

```csharp
public sealed class UserNames : StreamRequestBase<string>;

var dispatcher = new RecordingCqrsDispatcher()
    .SetupStream<UserNames, string>(_ => ["Ada", "Grace"]);

await foreach (var name in dispatcher.Stream(new UserNames()))
    Console.WriteLine(name);

Assert.Single(dispatcher.Streamed<UserNames>());
```

If the test project also references the CQRSharp source generator, request types declared there without a handler
raise the `CQRGEN003` warning. Reuse the request types of the application under test, declare test-only request
fixtures as private nested classes (the generator skips them), or add `CQRGEN003` to the test project's `NoWarn`.

## RecordingCqrsDispatcher reference

**Stubbing** (each returns the dispatcher, for chaining; the latest stub for a type wins):

| Member | Effect |
| --- | --- |
| `Setup<TRequest, TResponse>(Func<TRequest, TResponse> respond)` | Answers `TRequest` from the sent instance. |
| `Setup<TRequest, TResponse>(TResponse response)` | Answers every `TRequest` with a fixed value. |
| `SetupAsync<TRequest, TResponse>(Func<TRequest, CancellationToken, Task<TResponse>> respond)` | Asynchronous answer; receives the caller's token. |
| `SetupStream<TRequest, TItem>(Func<TRequest, IEnumerable<TItem>> items)` | Streams an in-memory sequence (cancellation honoured between items). |
| `SetupStream<TRequest, TItem>(Func<TRequest, CancellationToken, IAsyncEnumerable<TItem>> stream)` | Streams a real async sequence. |
| `Throws<TMessage>(Exception exception)` / `Throws<TMessage>(Func<TMessage, Exception> exceptionFactory)` | Makes a request, stream, or notification fail. Overrides a response stub for the same type. |

**Inspecting** (all are snapshots, in call order):

| Member | Contents |
| --- | --- |
| `SentRequests` / `Sent<T>()` | Requests passed to either `Send` overload. |
| `StartedStreams` / `Streamed<T>()` | Requests passed to either `Stream` overload, recorded when the stream is requested. |
| `PublishedNotifications` / `Published<T>()` | Notifications passed to `Publish`. |
| `Dispatched` | One interleaved log of `DispatchedMessage(DispatchKind Kind, object Message)` across all three. |
| `ClearRecorded()` | Empties the log; stubs are kept. |

**Behaviour worth knowing:**

- Stubs are matched by the request's runtime type, then by its nearest stubbed base class. Interfaces are not
  matched. `Sent<T>()` and friends filter by assignability, so `Published<INotification>()` returns everything.
- A missing stub throws **synchronously** from `Send` / `Stream` (it is a test-setup mistake). A failure configured
  with `Throws`, or thrown by a stub delegate, surfaces where a real handler failure would: as a faulted `Send` /
  `Publish` task, or on the first `MoveNextAsync` of a stream.
- A message is recorded even when dispatching it then fails, so a test can assert on what was attempted.
- The untyped `Send(object)` / `Stream(object)` overloads share the typed overloads' stubs and reject the same
  arguments as the real dispatcher: a non-`IRequest`, a stream request passed to `Send`, a non-stream passed to
  `Stream`. Rejected arguments are not recorded.
- It is thread-safe, and it runs no handlers, behaviors, validation, or notification handlers. To exercise those,
  build a container with `AddCqrs` as described in [Testing](testing.md).

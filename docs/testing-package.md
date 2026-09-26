# The testing packages

Two packages support your tests:

- **`CQRSharp.Testing.Xunit.V3`** - the store contract suites: the abstract xUnit v3 suites the built-in in-memory,
  EF Core, and Redis stores pass. Derive from them to prove a custom `IOutboxStore`, `IInboxStore` or
  `IIdempotencyStore` has the same semantics: for the outbox, atomic claims and leases, renew, release and deferral,
  retries and dead letters, FIFO order (`CreatedAt`, then store order), one message per handler with independent state,
  and partition ordering; for idempotency, claim tokens, stored results, payload fingerprints and expiry.
- **`CQRSharp.Testing`** - `RecordingCqrsDispatcher`, a fake `ICqrsDispatcher` for unit-testing code that *calls* the
  dispatcher (controllers, endpoints, application services) without a container or a pipeline. It depends on no test
  framework.

For testing handlers, behaviors, and the real dispatcher, see [Testing](testing.md).

```
dotnet add package CQRSharp.Testing.Xunit.V3   # the contract suites
dotnet add package CQRSharp.Testing            # RecordingCqrsDispatcher
```

Both put their types in the `CQRSharp.Testing` namespace.

## Contents

- [Dependencies](#dependencies)
- [Contract-testing an outbox store](#contract-testing-an-outbox-store)
- [Contract-testing an idempotency store](#contract-testing-an-idempotency-store)
- [Contract-testing an inbox store](#contract-testing-an-inbox-store)
- [Skipping when a backing service is absent](#skipping-when-a-backing-service-is-absent)
- [Unit-testing a consumer of ICqrsDispatcher](#unit-testing-a-consumer-of-icqrsdispatcher)
- [RecordingCqrsDispatcher reference](#recordingcqrsdispatcher-reference)

## Dependencies

The contract suites are written for **xUnit v3** (`xunit.v3` 4.x), so the test project that derives from them must be
an xUnit v3 test project (`xunit.v3`, `Microsoft.NET.Test.Sdk`, and `xunit.runner.visualstudio` if you run under
VSTest). `CQRSharp.Testing.Xunit.V3` depends only on `xunit.v3.extensibility.core`, `xunit.v3.assert`,
`Microsoft.Extensions.TimeProvider.Testing` (for `FakeTimeProvider`) and `CQRSharp.Abstractions`. It brings no
assertion or mocking library; failures are reported through plain `Xunit.Assert` with a message naming the violated
contract rule. Projects still on xUnit v2 cannot derive from the suites: migrate the test project to xUnit v3 first.

`CQRSharp.Testing` (`RecordingCqrsDispatcher`) depends on `CQRSharp.Core` and uses no test-framework types, so it works
under any test framework.

## Contract-testing an outbox store

Derive a sealed class from `OutboxStoreContractTests` and supply two things: the `FakeTimeProvider` the store reads
its clock from, and a factory for a fresh, empty store bound to that clock. The suite drives all timing (back-off,
visibility timeouts, lease renewal) by advancing the fake clock, so it never sleeps; a store that reads
`DateTime.UtcNow` directly instead of the injected `TimeProvider` will fail it.

```csharp
using CQRSharp.Persistence;
using CQRSharp.Testing;
using Microsoft.Extensions.Time.Testing;

public sealed class MyOutboxStoreTests : OutboxStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IOutboxStore> CreateStoreAsync()
        => Task.FromResult<IOutboxStore>(new MyOutboxStore(Time, visibilityTimeout: VisibilityTimeout));
}
```

xUnit creates a new instance of the class per test, so `Time` and the store start fresh every time. The suites
implement `IAsyncLifetime` with virtual `ValueTask InitializeAsync()` / `ValueTask DisposeAsync()` hooks: override
`DisposeAsync` to close
whatever `CreateStoreAsync` opened (a connection, a context), so a long suite does not leak one per test.

`VisibilityTimeout` is a virtual property (default five minutes). Configure the store with it, as above, or override
it to match a timeout the store fixes itself:

```csharp
protected override TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(1);
```

The suite covers:

- claiming: `ClaimPendingAsync` moves a message to `InProgress` and hands it out with an `OutboxClaim` (message id,
  token, lease) and every field it was stored with; FIFO order by `CreatedAt`, then store order; the batch-size limit;
  no double claim under concurrent `ClaimPendingAsync` calls; `JoinsUnitOfWork` is `false` outside a transaction;
- leases: a claimed message is not handed out again before its visibility timeout, and a crashed claimant's message is
  reclaimed after it; a stale claim changes nothing after a reclaim; `RenewAsync` extends a live lease and refuses one
  that ran out;
- outcomes: `MarkAsProcessedAsync` is terminal and idempotent; `IncrementAttemptAsync` counts the attempt, keeps the
  error, reschedules with `NextRetryAt` and never resurrects a processed message; `MarkAsFailedAsync` dead-letters;
  `DeferAsync` reschedules without counting an attempt and changes nothing for a processed or unknown message;
  `ReleaseAsync` makes a message claimable at once without counting an attempt;
- independence and order: messages for different handlers are independent; within a partition only the head is handed
  out, a back-off, a lease or a deferral holds the partition, a dead letter releases it, nothing overtakes an earlier
  message after a retry, a release or a requeue, and nothing joins a partition while one of its messages is in flight;
- dead letters: `GetDeadLettersAsync` lists them oldest first by failure time with their error; `RequeueAsync` gives a
  fresh budget, keeps the last error and waits for an in-flight successor; `PurgeDeadLettersAsync` deletes only what
  failed before the cut-off;
- the backlog: `GetBacklogAsync` counts undelivered and dead-lettered messages and reports the oldest pending one.

The single-instance suite cannot observe a double-claim between two *processes*. A store backed by a shared database
should add its own race test over two independent connections.

## Contract-testing an idempotency store

`IdempotencyStoreContractTests` takes the same shape: the `FakeTimeProvider` the store reads and a factory for a store
configured with `Retention` (a virtual property, default 24 hours; override it to match a retention the store fixes
itself). Every test uses a unique key, so one shared backing service can serve the whole run.

```csharp
using CQRSharp.Persistence;
using CQRSharp.Testing;
using Microsoft.Extensions.Time.Testing;

public sealed class MyIdempotencyStoreTests : IdempotencyStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IIdempotencyStore> CreateStoreAsync()
        => Task.FromResult<IIdempotencyStore>(new MyIdempotencyStore(Time, retention: Retention));
}
```

A store whose keys expire on its backing service's clock (the Redis store: Redis times the expiry) overrides
`SupportsClockDrivenRetention` to `false`, and the tests that advance the clock past the retention are skipped for it.

The suite covers: a fresh key is claimed; a second claim is rejected; an unfinished duplicate reports `InProgress`; a
completed key reports `Completed` and hands back the stored result byte-for-byte, an empty result as empty, and none
when completed with `null`; release frees an unfinished key but never a completed one; releasing or completing an
unknown key is a no-op; another claimant's token neither completes nor releases a claim; keys are case-sensitive; of
many concurrent claims of one key exactly one wins; an abandoned claim expires after the retention window, a stale
claimant can neither release nor complete its successor's claim, and completing a key keeps its claim's expiry; and
payload fingerprints: a key reused with a different fingerprint reports `PayloadMismatch` whether the original is
running or completed, a missing fingerprint on either side disables the comparison, and a release forgets the
fingerprint.

## Contract-testing an inbox store

`InboxStoreContractTests` takes the same shape as the outbox suite: the `FakeTimeProvider` the store reads and a
factory for a fresh store. `InboxRetention` (default seven days) is the retention the store is configured with; the
retention test advances the clock past it. A store whose records expire on its backing service's clock — the Redis
inbox uses a key TTL — overrides `SupportsClockDrivenRetention` to `false`, and that test is skipped for it.

```csharp
public sealed class MyInboxStoreTests : InboxStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IInboxStore> CreateStoreAsync()
        => Task.FromResult<IInboxStore>(new MyInboxStore(Time, retention: InboxRetention));
}
```

The suite covers: a delivery is unknown until recorded; a delivery is recorded exactly once; deliveries are
independent per message and per handler; of many concurrent records of one delivery exactly one wins; a record is
forgotten once the retention has elapsed; and `JoinsUnitOfWork` is `false` outside a transaction.

## Skipping when a backing service is absent

xUnit v3 skips a test at run time from anywhere in it, so `CreateStoreAsync` may skip instead of fail when the store's
backing service is not reachable (a Redis or database server that only exists in CI, say):

```csharp
protected override async Task<IIdempotencyStore> CreateStoreAsync()
{
    Assert.SkipUnless(_fixture.Available, "No server is reachable.");
    return new MyIdempotencyStore(await _fixture.ConnectAsync());
}
```

`Assert.SkipUnless` / `Assert.SkipWhen` are xUnit v3's own (namespace `Xunit`).

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
            .Setup<RenameUser, CommandResult>(CommandResult.Conflict("Name is taken."));
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
  build a host with `AddCqrsGenerated` as described in [Testing](testing.md#testing-through-the-dispatcher).

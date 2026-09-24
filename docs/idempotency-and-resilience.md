# Idempotency &amp; resilience

Two related reliability behaviors: **idempotency** makes a repeated request run at most once and answers the repeat
with the original result, and **resilience** retries transient failures. They compose with **timeouts** and the
[unit of work](unit-of-work.md) in a fixed order.

- [Idempotency](#idempotency)
- [Resilience &amp; retries](#resilience--retries)
- [Timeouts](#timeouts)
- [How they compose](#how-they-compose)

## Idempotency

Idempotency enforces **at-most-once** processing. Mark a request with `IIdempotentRequest` and give it a stable key:

```csharp
public sealed class ChargeCard : CommandBase, IIdempotentRequest
{
    public required string PaymentId { get; init; }
    public string IdempotencyKey => $"charge:{PaymentId}";
}
```

Enable the behavior and a store with `UseIdempotency`:

```csharp
services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()));   // development
// durable: i.UseRedis(...) or i.UseEntityFrameworkCore<AppDbContext>(); see Integrations
```

`IdempotencyStoreBuilder` (namespace `CQRSharp.Pipelines`) offers `UseInMemoryStore(configure?)`, `UseRedis(...)`,
`UseEntityFrameworkCore<TContext>()` (see [Integrations](integrations.md)), the `UseStore(Action<IServiceCollection>)`
hook for a custom store, and `ReplayResultsWith(...)` (below). A chosen store replaces the idempotency store already
registered, whatever the order; a bare `UseIdempotency()` uses the in-memory store only when no idempotency store is
registered at all.

If a request implements `IIdempotentRequest` but `UseIdempotency(...)` is never called, the marker does nothing and
duplicates are processed. The startup validator reports this as **CQRCONF005**, an error, so `ValidateOnStart()` stops
the host (see [Diagnostics](diagnostics.md#startup-validation-cqrconf)).

When a request implementing `IIdempotentRequest` is dispatched, the behavior **claims** its key before the handler runs.
The store answers one of four things:

| Claim status | Meaning | What the caller gets |
| --- | --- | --- |
| `Claimed` | The key was free (or its earlier claim expired). | The handler runs. When the request completes, the key is **completed** with its result; when it fails, the key is **released**, so a retry with the same key runs it again. |
| `Completed` | A request with this key already completed. | **The original result, replayed**; the handler does not run again. This is what a key is for: the client retried because it never saw the response. When the result cannot be replayed, `DuplicateRequestException` with `IsInProgress == false`. |
| `InProgress` | The original is still running (or crashed, and its claim has not expired yet). | `DuplicateRequestException` with `IsInProgress == true`; the caller can retry shortly. |
| `PayloadMismatch` | The key was used by a request with a different payload fingerprint. | `IdempotencyKeyMismatchException`; see [key reuse](#key-reuse-with-a-different-payload). |

A request **fails**, and releases its key, when it throws, is canceled, or is a command that *returns* a failed
`CommandResult`. The one exception: a failed result that its unit of work commits
(`UnitOfWorkOptions.RollbackOnFailedResult = false`, see [Unit of work](unit-of-work.md#what-counts-as-failure)). That
work stands, so the key is completed and a duplicate gets the same failure back.

**Streaming requests** are covered too: the key is claimed when enumeration starts and completed only if the stream is
enumerated to its end; a stream that faults, or whose consumer stops early, releases it. A stream's items are not
stored, so a duplicate of a completed stream is always rejected with `DuplicateRequestException`.

An idempotent request with an empty key throws `InvalidOperationException` before anything is claimed.

### Key reuse with a different payload

A key identifies *one* logical request, so the same key with a different payload is a client error, not a retry. Every
claim carries a **fingerprint** of the request's payload; a later claim of the same key with a different fingerprint is
rejected with `IdempotencyKeyMismatchException`, neither replayed nor run. [CQRSharp.AspNetCore](aspnetcore.md) maps it
to `422 Unprocessable Content`, as the IETF `Idempotency-Key` draft asks.

The fingerprint comes from one of two places:

- **Automatic.** The source generator renders the request's properties (all but its `Context`) and the behavior hashes
  them with SHA-256. The rendering is the outbox serializer's, so the same [shapes](outbox.md#supported-shapes) are
  supported, an unconditional `[JsonIgnore]` leaves a property out, dictionary entries and set elements are ordered so
  equal payloads fingerprint alike however they were built. A request whose properties cannot be rendered gets no
  fingerprint and is reported as **CQRGEN014** (Info). For a request declared in an assembly the generator does not run
  in, the assembly that handles it computes the fingerprint; members internal to another assembly are invisible there
  and are left out.
- **Your own.** Implement `IFingerprintedRequest` (namespace `CQRSharp`) to decide what counts, for instance to leave out
  a client timestamp that legitimately differs between retries:

  ```csharp
  public sealed class Transfer : CommandBase, IFingerprintedRequest
  {
      public required decimal Amount { get; init; }
      public required DateTime RequestedAt { get; init; }
      public required string IdempotencyKey { get; init; }
      public string Fingerprint => FormattableString.Invariant($"amount:{Amount}");
  }
  ```

  Any stable string of any length will do; render numbers and dates with the invariant culture. An empty or `null`
  fingerprint turns the payload check off for that request.

Either way the request's **type** is part of the fingerprint: two request types that send the same payload under one
key are a mismatch, never a replay of each other. Renaming or moving a request type changes its fingerprints. The store
receives a SHA-256 digest of 64 hexadecimal characters. A claim without a fingerprint, on either side, is compared on
the key alone.

### What can be replayed

- A plain **`CommandResult`** is always replayed, with no configuration: a success as `CommandResult.FromSuccess()`,
  and a failure its unit of work committed as that same failure (kind, message, code and validation failures).
- A **value-carrying result** (`CommandResult<T>`, a query result) has to be stored, so it needs a result serializer:

  ```csharp
  services.AddCqrsGenerated(b => b.UseIdempotency(i => i
      .UseRedis(connectionString)
      .ReplayResultsWith(new JsonSerializerOptions { TypeInfoResolver = AppJsonContext.Default })));
  ```

  The options need a `TypeInfoResolver` on every runtime, or `ReplayResultsWith` throws `ArgumentException`: your
  source-generated `JsonSerializerContext` (required for Native AOT and trimming; list your result types, e.g.
  `[JsonSerializable(typeof(CommandResult<Receipt>))]`), or `new DefaultJsonTypeInfoResolver()` where reflection is
  available. A result type the resolver cannot handle is not replayed, and a warning (event 4109, see
  [Observability](observability.md#logging)) is logged once per request type. For another format, implement
  `IIdempotencyResultSerializer` (namespace `CQRSharp.Persistence`) and pass it to `ReplayResultsWith(serializer)`; a
  later call replaces an earlier one.
- A value-carrying result with no serializer configured, or one the serializer cannot store, is not replayed: its
  duplicate gets `DuplicateRequestException` with `IsInProgress == false`.

### Claims and the retention window

A claim that is never completed or released (its process crashed) is taken over once the store's retention window
elapses, so no key stays blocked for good. The retention window is also the deduplication window: a request whose key
was claimed longer ago than that is no longer recognised as a duplicate. And it is the longest a request may run and
still be protected from a concurrent duplicate. Each store's retention is set on its options (`Retention`, 24 hours by
default); the in-memory and EF Core stores accept up to 10 years, and a value out of range fails host start.

### The store contract

`IIdempotencyStore` (namespace `CQRSharp.Persistence`) has three members:

```csharp
public interface IIdempotencyStore
{
    Task<IdempotencyClaim> TryClaimAsync(string key, string? fingerprint, CancellationToken cancellationToken);
    Task CompleteAsync(string key, string claimToken, byte[]? result, CancellationToken cancellationToken);
    Task ReleaseAsync(string key, string claimToken, CancellationToken cancellationToken);
}
```

- `TryClaimAsync` answers atomically with an `IdempotencyClaim` built from its factories:
  `IdempotencyClaim.ClaimedWith(token)` (the token unique to this claim), `IdempotencyClaim.InProgress`,
  `IdempotencyClaim.PayloadMismatch` or `IdempotencyClaim.Completed(result)`. Of many concurrent claims of one key,
  exactly one wins. A store that answers `null` fails the request before the handler runs, with an
  `InvalidOperationException` naming the store.
- `CompleteAsync` keeps the key, stores the result (or none) and leaves the claim's expiry as it was; `ReleaseAsync`
  forgets the claim. Both act only while the key still carries the claim `claimToken` names, so a request that outlived
  the retention window, and whose key was taken over, cannot complete or release its successor's claim.
- A store remembers the fingerprint a key was claimed with and answers `PayloadMismatch` for a different non-null one,
  whether the original is running or completed. A release forgets it.
- Keys compare ordinally and case-sensitively.

Verify a custom store against the contract suite; see
[The testing packages](testing-package.md#contract-testing-an-idempotency-store).

### Key guidance

- Keys must be **stable** for one logical request and **unique across every caller**. The stores' key space is global:
  it is not scoped by caller, tenant or request type. Prefix a client-supplied key with the caller's or tenant's
  identity (see [ASP.NET Core](aspnetcore.md#idempotency-key)), and make a derived key include it. Two users whose
  requests produce the same key would otherwise have the second one answered with the first one's result.
- Comparison is **ordinal and case-sensitive**: `Foo` and `foo` are different requests. On SQL Server, map the EF Core
  key column with a binary collation (see [Integrations](integrations.md#mapping-the-tables)).
- Keep keys within **450 characters** (the EF Core store's limit, and the most a SQL Server primary key holds); hash
  longer natural keys first. The in-memory and Redis stores accept longer keys, so a key that works there can fail on
  EF Core.

## Resilience &amp; retries

The resilience behavior **retries** a failing request. A retry runs the handler again, so only requests that are safe to
run twice should opt in, with `IRetryableRequest` (namespace `CQRSharp`):

```csharp
public sealed class FetchRate : QueryBase<decimal>, IRetryableRequest
{
    public required string Symbol { get; init; }
}
```

Enable it with `UseResilience`:

```csharp
.UseResilience(o =>
{
    o.MaxRetries        = 5;
    o.BaseDelay         = TimeSpan.FromMilliseconds(200);
    o.BackoffMultiplier = 2.0;                 // exponential
    o.MaxDelay          = TimeSpan.FromSeconds(10);
})
```

`ResilienceOptions` (namespace `CQRSharp.Pipelines`); invalid values fail host start:

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxRetries` | `3` | Retries after the first attempt: `3` means up to four attempts. `0` turns retries off. Not negative. |
| `BaseDelay` | `1s` | The delay before the first retry; `TimeSpan.Zero` retries at once. Not negative. |
| `BackoffMultiplier` | `1.0` | The factor each further delay grows by: `1.0` keeps every delay at `BaseDelay`, `2.0` doubles it. A finite number of at least 1. |
| `MaxDelay` | `30s` | The longest delay. At least `BaseDelay`. |

The delay before retry *n* is `BaseDelay × BackoffMultiplier^(n-1)`, capped at `MaxDelay`, and is waited on the
configured `TimeProvider`.

A request that does not implement `IRetryableRequest` passes straight through: no span, no log, no retry. Only
exceptions are retried; a command that *returns* a failed `CommandResult` is not. Even for a retryable request, a
failure that another attempt cannot change propagates at once:

- cancellation by the caller (an `OperationCanceledException` while the caller's token is canceled);
- `RequestTimeoutException`, the [timeout behavior's](#timeouts) own;
- `RateLimitExceededException`;
- `DuplicateRequestException` and `IdempotencyKeyMismatchException`;
- `RequestValidationException`.

Any other exception is retried, including a `TimeoutException` or an `OperationCanceledException` raised by a dependency
(a database driver, an `HttpClient` timeout).

A **streaming request** is retried only while it has yielded no item: once an item has reached the consumer, a failure
propagates, so no item is delivered twice.

If a request implements `IRetryableRequest` but `UseResilience(...)` is never called, the marker does nothing; the
startup validator reports **CQRCONF006**, a warning.

## Timeouts

`UseTimeout` bounds each attempt with a time budget:

```csharp
.UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(10))   // default: 30 s; must be greater than zero
```

When the time runs out, the behavior cancels the token the handler receives and, once the handler observes the
cancellation, throws `RequestTimeoutException` (namespace `CQRSharp`), a `TimeoutException` that carries `RequestType`
and `Timeout`. The timeout is **cooperative**: a handler that ignores its token runs to completion, and its result is
returned. For a streaming request the budget covers the whole enumeration.

The timeout runs closest to the handler, inside resilience, so it bounds one attempt, not the retries. A timed-out
attempt is not retried: retrying would multiply the budget by the retry count. [CQRSharp.AspNetCore](aspnetcore.md)
maps `RequestTimeoutException` to `504`.

## How they compose

The built-in behaviors run in a fixed order, outermost first (the full order is in
[Pipeline behaviors](pipeline-behaviors.md#execution-order)):

```
Resilience( Idempotency( UnitOfWork( Timeout( handler ) ) ) )
```

- **Resilience wraps the unit of work**, so each retry starts after the failed attempt was rolled back, transaction and
  pending changes, and runs in a transaction of its own.
- **Idempotency sits inside resilience and outside the unit of work.** A claim released by a failed attempt is claimed
  again by the next one, and a duplicate is rejected with `DuplicateRequestException` before any transaction opens.
- **Timeout is innermost**, bounding the handler (and any custom behavior without a priority) in each attempt.

So a retryable, idempotent, transactional command behaves as one expects: a transient failure rolls back the
transaction, releases the idempotency claim, waits the back-off and tries again from a clean state, while a genuine
duplicate is rejected, or answered with the original result, before any work is done.

# Idempotency &amp; resilience

Two related reliability behaviors: **idempotency** rejects duplicate executions of the same logical
request, and **resilience** automatically retries transient failures. They compose with **timeouts**
and the [unit of work](unit-of-work.md) in a deliberate order.

- [Idempotency](#idempotency)
- [Resilience &amp; retries](#resilience--retries)
- [Timeouts](#timeouts)
- [How they compose](#how-they-compose)

## Idempotency

Idempotency enforces **at-most-once** processing. Mark a request with `IIdempotentRequest` and give it a
stable key:

```csharp
public sealed class ChargeCard : CommandBase, IIdempotentRequest
{
    public required string PaymentId { get; init; }
    public string IdempotencyKey => $"charge:{PaymentId}";
}
```

Enable the behavior and a store with `UseIdempotency`:

```csharp
services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore()));   // dev
// durable: i.UseRedis(...) or i.UseEntityFrameworkCore<AppDbContext>()
```

When a request implementing `IIdempotentRequest` is dispatched, the behavior **claims** its key before
the handler runs:

1. `IIdempotencyStore.TryClaimAsync(key)` is called. If it returns `false`, the key is already claimed —
   the request is a duplicate and the behavior throws `DuplicateRequestException`.
2. If it returns `true`, the handler runs. On **failure**, the claim is **released**
   (`ReleaseAsync(key)`) so a later attempt (including a resilience retry) can re-process it; on success
   the claim stands.

`IIdempotencyStore` is small:

```csharp
public interface IIdempotencyStore
{
    Task<bool> TryClaimAsync(string key, CancellationToken ct);   // true = newly claimed, proceed
    Task ReleaseAsync(string key, CancellationToken ct);
}
```

`IdempotencyStoreBuilder` offers `UseInMemoryStore(configure?)`, `UseRedis(...)`,
`UseEntityFrameworkCore<TContext>()`, and the `UseStore(Action<IServiceCollection>)` hook for a custom
store. A bare `UseIdempotency()` uses the in-memory store.

### Key guidance

- Keys must be **stable** for a given logical request and **unique** across distinct requests (a
  client-supplied request id, or a deterministic hash of the meaningful inputs).
- Comparison is **ordinal, case-sensitive** — `Foo` and `foo` are different requests. A relational store
  whose column collation folds case needs a binary/case-sensitive collation to honor this.
- Keep keys within **512 characters** for portability across backends; hash longer natural keys first.
- An empty key throws — if a request declares `IIdempotentRequest` it must supply a real key.

> **Don't forget to enable the behavior.** If a request implements `IIdempotentRequest` but you never
> call `UseIdempotency(...)`, the marker silently does nothing. The startup validator catches this and
> reports **CQRCONF005**.

## Resilience &amp; retries

The resilience behavior automatically **retries** a failing request. Retrying re-invokes the handler,
so only **idempotent** requests should be retried — opt in explicitly with `IRetryableRequest`:

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

`ResilienceOptions`:

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxRetries` | `3` | Maximum retry attempts after the first try. |
| `BaseDelay` | `1s` | Delay before the first retry. `TimeSpan.Zero` disables delays. |
| `BackoffMultiplier` | `1.0` | Per-attempt multiplier — `1.0` = fixed, `2.0` = exponential back-off. |
| `MaxDelay` | `30s` | Cap on the computed back-off delay. |

The delay for attempt *n* is `BaseDelay × BackoffMultiplier^(n-1)`, capped at `MaxDelay`
(`ComputeRetryDelay`).

> **Never retried:** caller cancellation (`OperationCanceledException`) and timeouts
> (`TimeoutException`) are propagated immediately, even for retryable requests — a cancelled or
> timed-out request is not transient.

A request that does **not** implement `IRetryableRequest` is never retried; its first failure
propagates. If a request implements `IRetryableRequest` but you never call `UseResilience(...)`, the
marker silently does nothing — the startup validator reports **CQRCONF006**.

## Timeouts

`UseTimeout` bounds the handler with a per-request timeout. It runs **closest to the handler** so it
times the handler itself, not the outer behaviors (including retries):

```csharp
.UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(10))   // default: 30s
```

When the timeout elapses the handler is cancelled and a `TimeoutException` is raised. Because resilience
runs *outside* timeout, a `TimeoutException` is **not** retried (see above) — tune `MaxRetries` and the
timeout together if you want a bounded retry-on-slow policy via a different exception.

## How they compose

These behaviors are ordered deliberately (outermost to innermost; see
[Pipeline behaviors](pipeline-behaviors.md#execution-order)):

```
Resilience(  Idempotency(  UnitOfWork(  Timeout(  handler  ))))
```

- **Resilience wraps the unit of work**, so each retry executes against a **fresh transaction** rather
  than reusing a poisoned one.
- **Idempotency sits inside resilience** (a claim released on failure can be re-claimed by the next
  retry) but **outside the unit of work** (a duplicate short-circuits with `DuplicateRequestException`
  *before* any transaction opens).
- **Timeout is innermost**, bounding the handler each attempt.

This is why a retryable, idempotent, transactional command behaves correctly: a transient failure rolls
back the transaction, releases the idempotency claim, waits the back-off, and tries again on a clean
slate — while a genuine duplicate is rejected up front with no wasted work.

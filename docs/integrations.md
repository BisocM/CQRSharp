# Integrations

The outbox and idempotency features need a **store**. The core package ships in-memory stores for
development; for durable, production persistence, add an integration package and select its store verb
inside `UseOutbox` / `UseIdempotency`.

- [Redis](#redis)
- [Entity Framework Core](#entity-framework-core)
- [Choosing a store](#choosing-a-store)
- [Writing your own](#writing-your-own)

## Redis

```bash
dotnet add package CQRSharp.Redis
```

`CQRSharp.Redis` provides a durable `IOutboxStore` and `IIdempotencyStore` backed by Redis, and is
**100% Native-AOT-clean**. All claim, lease, and finalize logic runs as **atomic server-side Lua**, so
two processors never claim the same outbox message and crashed claimants are reclaimed after a
visibility timeout. The idempotency store rejects duplicates via atomic set-if-not-exists claims with a
server-side expiry window.

Select it through the store builders:

```csharp
services.AddCqrsGenerated(b => b
    .UseOutbox(o => o.Transactional().UseRedis("localhost:6379"))
    .UseIdempotency(i => i.UseRedis("localhost:6379")));
```

`UseRedis(...)` accepts either a connection string or an existing `IConnectionMultiplexer` (so you can
share one multiplexer across your app). The same lower-level extensions —
`AddRedisOutboxStore(...)` / `AddRedisIdempotencyStore(...)` — are available on `IServiceCollection` if
you wire stores outside the builder.

## Entity Framework Core

```bash
dotnet add package CQRSharp.EntityFrameworkCore
```

`CQRSharp.EntityFrameworkCore` provides a durable `IOutboxStore` and `IIdempotencyStore` backed by a
`DbContext`: the outbox uses an atomic claim with an optimistic-concurrency **visibility lease**,
persisted retry/back-off, and dead-lettering; the idempotency store uses a keyed table with an
optimistic-concurrency claim and a retention window.

```csharp
services.AddCqrsGenerated(b => b
    .UseOutbox(o => o.Transactional().UseEntityFrameworkCore<AppDbContext>())
    .UseIdempotency(i => i.UseEntityFrameworkCore<AppDbContext>()));
```

The store participates in your `DbContext`'s transaction, which is what makes the **transactional
outbox** atomic with your business writes. Configure your `DbContext` to include the outbox/idempotency
entities per the package's model setup.

> **Not Native-AOT compatible.** EF Core uses runtime query compilation, so the EF integration is marked
> `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` and is **not** AOT- or full-trim-safe. If you
> publish with Native AOT, use the Redis store (or a custom AOT-safe store) instead. See
> [Native AOT](native-aot.md).

## Choosing a store

| Store | Package | Durable | Native AOT | Concurrency model |
| --- | --- | --- | --- | --- |
| In-memory | `CQRSharp.Core` | No | Yes | In-process only |
| Redis | `CQRSharp.Redis` | Yes | Yes | Atomic server-side Lua claim + visibility timeout |
| EF Core | `CQRSharp.EntityFrameworkCore` | Yes | No | Optimistic-concurrency claim lease |

The outbox and idempotency stores are chosen **independently** — you can run a Redis outbox with an EF
idempotency store, or any other mix.

## Writing your own

Both store contracts are small and documented:

- [`IOutboxStore`](outbox.md#custom-stores) — `StoreAsync`, `GetPendingAsync` (atomic claim),
  `MarkAsProcessedAsync`, `IncrementAttemptAsync` (retry/back-off), `MarkAsFailedAsync` (dead-letter).
- [`IIdempotencyStore`](idempotency-and-resilience.md#idempotency) — `TryClaimAsync`, `ReleaseAsync`.

Register a custom store through the `UseStore(Action<IServiceCollection>)` hook on either builder:

```csharp
.UseOutbox(o => o.Transactional().UseStore(s => s.AddSingleton<IOutboxStore, MyStore>()))
```

Then verify it against the shared **store contract tests** before relying on it — see
[Testing](testing.md). Honoring the contract (atomic claims, persisted retry counts, case-sensitive
idempotency keys) is what guarantees correct behavior under concurrency and restarts.

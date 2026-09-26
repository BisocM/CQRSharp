# Integrations

The [outbox](outbox.md) and [idempotency](idempotency-and-resilience.md#idempotency) features need a **store**. The core
package ships in-memory stores for development; for durable storage, add an integration package and select its store
verb inside `UseOutbox` / `UseIdempotency`.

- [Redis](#redis)
- [Entity Framework Core](#entity-framework-core)
- [Choosing a store](#choosing-a-store)
- [Writing your own](#writing-your-own)

## Redis

```bash
dotnet add package CQRSharp.Redis
```

`CQRSharp.Redis` provides a durable `IOutboxStore` with its `IInboxStore`, and a durable `IIdempotencyStore`, backed by
StackExchange.Redis. It is Native-AOT compatible. Every outbox claim, lease and finalize runs as one server-side Lua
script, so two processors never claim one message and a crashed claimant's messages are reclaimed after the visibility
timeout. The idempotency store claims a key atomically, remembers the payload fingerprint, and keeps a completed
request's result for replay.

```csharp
services.AddCqrsGenerated(b => b
    .UseOutbox(o => o.UseRedis("localhost:6379"))
    .UseIdempotency(i => i.UseRedis("localhost:6379")));
```

The Redis store does not join a unit-of-work transaction: a transactional request's notifications are stored right
after its commit (see [How a publish reaches the store](outbox.md#how-a-publish-reaches-the-store)).

### Connections

Each `UseRedis` verb, on either builder, takes the connection in one of three forms, and each store runs on exactly the
connection it is given, whatever `IConnectionMultiplexer` the application registers:

| Form | Lifetime |
| --- | --- |
| `UseRedis("host:6379")`, a connection string | Opened when a store is first resolved, shared by every CQRSharp Redis store given the same string, and closed when the service provider is disposed. |
| `UseRedis(multiplexer)`, an `IConnectionMultiplexer` | Used as given; CQRSharp never disposes it. |
| `UseRedis(sp => ...)`, a factory | Runs once per service provider for each store (the outbox and its inbox share one call). It must return a connection the application or the container owns; CQRSharp never disposes it. |

The factory form fits a connection the container already holds, such as Aspire's `AddRedisClient` or a keyed
registration:

```csharp
.UseOutbox(o => o.UseRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>()))
```

CQRSharp.Redis registers no `IConnectionMultiplexer` in the container. The outbox and the idempotency store may use
different servers: pass each builder its own connection. Outside the builder, `AddRedisOutboxStore(...)` and
`AddRedisIdempotencyStore(...)` on `IServiceCollection` take the same three forms plus an optional options callback. A
blank connection string is rejected when the verb is called.

### Options

`RedisOutboxOptions` (namespace `CQRSharp.Redis`), set through `UseRedis(connection, o => ...)`:

| Option | Default | Meaning |
| --- | --- | --- |
| `KeyPrefix` | `{cqrsharp:outbox}:` | Prefix of every key the outbox and inbox use. Must contain a non-empty hash tag; see below. |
| `VisibilityTimeout` | `5 min` | How long a claim is leased before another processor may reclaim the message. At least 1 ms. |
| `DeadLetterRetention` | `null` | How long a dead letter is kept, from when it failed. `null` keeps dead letters until they are requeued or purged. At least 1 ms when set. |
| `InboxRetention` | `7 days` | How long an inbox record is kept. Must comfortably exceed `VisibilityTimeout`. At least 1 ms. |
| `Database` | `-1` | The logical database; `-1` is the connection's default. |

`RedisIdempotencyOptions`:

| Option | Default | Meaning |
| --- | --- | --- |
| `KeyPrefix` | `{cqrs:idemp}:` | Prefix of the store's keys: `{KeyPrefix}k:{key}` holds a key's claim or result, `{KeyPrefix}f:{key}` its fingerprint. Must contain a non-empty hash tag. |
| `Retention` | `24 h` | How long a claimed key is remembered, counted from the claim: the deduplication window. Redis times the expiry. At least 1 ms. |
| `Database` | `-1` | The logical database; `-1` is the connection's default. |

Invalid values fail host start, each reported once.

- **Hash tag.** A store's scripts touch several keys at once, which Redis Cluster allows only when every key hashes to
  one slot. A key with a hash tag (the text between the first `{` and the next `}`) is slotted by the tag alone, so
  the tag in the prefix keeps all of a store's keys in one slot. The rule is enforced on a single server too, so a
  store keeps working when it moves to a cluster. Changing a prefix does not move data stored under the old one.
- **Visibility timeout.** The processor renews a message's lease just before dispatching it once half the lease has
  passed, but never while its handler runs. Set `VisibilityTimeout` comfortably above twice the slowest single
  handler; a longer value only delays recovery after a crash. A message whose lease lapsed and was reclaimed before
  its turn is skipped, not delivered twice.
- **Retention.** A processed message is deleted as soon as it is marked processed; the inbox is what recognises a
  redelivery of it, for `InboxRetention`. Dead letters live in their own sorted set, listed, requeued and purged
  through `IOutboxStore`. An inbox record is `{KeyPrefix}inbox:{messageId}:{handler}` with a TTL of `InboxRetention`.

## Entity Framework Core

```bash
dotnet add package CQRSharp.EntityFrameworkCore
```

`CQRSharp.EntityFrameworkCore` provides, over your `DbContext`:

- a durable `IOutboxStore` with its `IInboxStore`: a claim backed by a row version, persisted retries and dead letters;
- a durable `IIdempotencyStore`: a keyed table with an optimistic-concurrency claim and a retention window;
- `EfCoreUnitOfWork<TContext>`, the unit of work over the context's transaction.

Each target framework of the package is built against its own EF Core major: `net8.0` against EF Core 8 (8.0.10 or
later, below 9), `net9.0` against EF Core 9 (9.0.2 or later), `net10.0` against EF Core 10 (10.0.0 or later). An app on
`net8.0` cannot use EF Core 9; target `net9.0` for it.

```csharp
services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));

services.AddCqrsGenerated(b => b
    .UseEntityFrameworkCoreUnitOfWork<AppDbContext>()
    .UseOutbox(o => o.UseEntityFrameworkCore<AppDbContext>())
    .UseIdempotency(i => i.UseEntityFrameworkCore<AppDbContext>()));
```

None of the verbs registers the `DbContext`; register it yourself. `UseEntityFrameworkCoreUnitOfWork<TContext>()` is
`UseUnitOfWork(sp => new EfCoreUnitOfWork<TContext>(sp.GetRequiredService<TContext>()))`; see
[Unit of work](unit-of-work.md#the-entity-framework-core-unit-of-work) for its semantics. Outside the builder,
`AddEntityFrameworkCoreOutboxStore<TContext>()` and `AddEntityFrameworkCoreIdempotencyStore<TContext>()` register the
stores.

The outbox store and its inbox are scoped and write through the scope's `TContext`. While a transaction is open on that
context (an `EfCoreUnitOfWork<TContext>` over the same instance, or one the application began), they **join** it: a
request's notifications, or a delivery's inbox record, commit with the handler's changes. Without a transaction, a
direct store (a publish from outside any request, or the end-of-request store in `Enabled` mode) calls
`SaveChangesAsync` on the scoped context, which also saves every other change tracked on it: save or discard your own
changes before publishing. The idempotency store is the opposite: it opens its own scope and context for every
operation, so a claim never joins, or saves, the work of the request it guards.

### Mapping the tables

Map the tables in your `DbContext`, then add a migration:

```csharp
using CQRSharp.EntityFrameworkCore;

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.ApplyCqrsOutbox();                                     // CqrsOutboxMessages and CqrsInboxRecords
    modelBuilder.ApplyCqrsIdempotency(
        IdempotencyEntityConfiguration.SqlServerBinaryCollation);       // CqrsIdempotencyKeys; SQL Server only
}
```

- Idempotency keys compare ordinally and case-sensitively. On SQL Server, whose default collation folds case, pass
  `IdempotencyEntityConfiguration.SqlServerBinaryCollation` (`Latin1_General_100_BIN2`); on SQLite and PostgreSQL call
  `ApplyCqrsIdempotency()` without a collation.
- Both stores check the context's **model** at host start: a context that does not map the tables, or on SQL Server an
  idempotency key column without a case-sensitive collation, fails start with the fix in the message. The database
  schema itself is not checked, so apply the migration.
- The idempotency key column holds up to 450 characters (`IdempotencyEntityConfiguration.KeyMaxLength`) and the
  fingerprint up to 128 (`FingerprintMaxLength`); the store refuses a longer one with an `InvalidOperationException`
  before touching the database. The outbox holds handler names and partition keys of up to 256 characters
  (`OutboxEntityConfiguration.HandlerNameMaxLength` and `PartitionKeyMaxLength`).
- The entity types (`OutboxEntity`, `InboxEntity`, `IdempotencyEntity`) have virtual properties and change
  notifications, so they map in contexts that use lazy-loading or change-tracking proxies.

Upgrading from 4.x changes the schema; the [CHANGELOG](../CHANGELOG.md) lists the migration steps.

### Options and retention

`EfCoreOutboxStoreOptions` (namespace `CQRSharp.EntityFrameworkCore`), set through
`UseEntityFrameworkCore<TContext>(o => ...)`:

| Option | Default | Meaning |
| --- | --- | --- |
| `VisibilityTimeout` | `5 min` | How long a claim is leased before another processor may reclaim the message. |
| `MaxClaimAttempts` | `3` | How often a batch claim that lost the row-version race to another processor is tried again before the contested messages are left for the next poll. At least 1. |
| `ProcessedRetention` | `7 days` | How long a processed message is kept; `null` keeps processed messages forever. |
| `DeadLetterRetention` | `null` | How long a dead letter is kept, from when it failed; `null` keeps dead letters until they are requeued or purged. |
| `InboxRetention` | `7 days` | How long an inbox record is kept. Must comfortably exceed `VisibilityTimeout`. |
| `PurgeInterval` | `1 h` | The time between two purges. At most the longest delay a timer can wait (about 49 days). |

`EfCoreIdempotencyStoreOptions.Retention` (default `24 h`) is how long a claimed key is remembered: the deduplication
window.

Every duration must be greater than zero, and at most 10 years except `PurgeInterval`; a value out of range fails host
start, each reported once.

Retention runs in hosted services the store verbs register, never on a request or a claim. The outbox service deletes
processed messages, dead letters past `DeadLetterRetention` and inbox records when the host starts and then every
`PurgeInterval`, and is idle while the outbox is off. The idempotency service deletes expired keys when the host starts
and then every `Retention`, at least hourly. Both delete in pages of 1,000 rows, and a failed purge is logged and tried
again an interval later. An app without the generic host runs no hosted services, and so gets no retention.

> **Execution strategies.** `EfCoreUnitOfWork` refuses to begin a transaction on a context configured with a retrying
> execution strategy (`EnableRetryOnFailure`), which rejects a transaction the application begins itself. Retry whole
> requests with [`UseResilience`](idempotency-and-resilience.md#resilience--retries) instead.

> **Not Native-AOT compatible.** EF Core compiles queries at runtime, so the package's registration verbs are annotated
> `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]`. If you publish with Native AOT, use the Redis store or an
> AOT-safe store of your own; see [Native AOT](native-aot.md).

## Choosing a store

| Store | Package | Durable | Native AOT | Claims | Joins the unit of work |
| --- | --- | --- | --- | --- | --- |
| In-memory | `CQRSharp.Core` | No | Yes | In-process lock | No |
| Redis | `CQRSharp.Redis` | Yes | Yes | Server-side Lua script and visibility timeout | No |
| EF Core | `CQRSharp.EntityFrameworkCore` | Yes | No | Row-version claim and visibility timeout | Yes, while a transaction is open on its context |

A store that joins the unit of work makes a transactional request's notifications atomic with its data, and, with the
inbox, a delivery exactly-once for what its handler writes through that context. Any other store is written right after
the commit; see [How a publish reaches the store](outbox.md#how-a-publish-reaches-the-store) and
[The inbox](outbox.md#the-inbox-effectively-once-delivery).

The outbox and idempotency stores are chosen independently: a Redis outbox with an EF Core idempotency store, or any
other mix. Every explicit store registration, builder verb or `Add*Store` method, replaces the store of its kind that is
already registered, whatever the order relative to `AddCqrsGenerated`; the last explicit choice wins, and the outbox and
inbox stores are always replaced as a pair.

## Writing your own

Implement the contracts in `CQRSharp.Persistence` and register them through the `UseStore(Action<IServiceCollection>)`
hook on either builder:

- `IOutboxStore` and `IInboxStore`: see [Custom stores](outbox.md#custom-stores).
- `IIdempotencyStore`: see [Idempotency](idempotency-and-resilience.md#the-store-contract).

Then verify the store against the contract suites in `CQRSharp.Testing.Xunit.V3`; see
[The testing packages](testing-package.md#contract-testing-an-outbox-store).

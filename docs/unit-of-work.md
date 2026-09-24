# Unit of work &amp; transactions

The unit-of-work behavior runs a request in a transaction, so its writes, and the [outbox](outbox.md) notifications it
publishes, commit or roll back together. CQRSharp does not own your data access: you register an `IUnitOfWork` over it,
the one the EF Core package ships or your own, and the behavior drives it.

- [The contract](#the-contract)
- [Enabling the behavior](#enabling-the-behavior)
- [The Entity Framework Core unit of work](#the-entity-framework-core-unit-of-work)
- [Marking transactional requests](#marking-transactional-requests)
- [What counts as failure](#what-counts-as-failure)
- [Transactions that are already open](#transactions-that-are-already-open)
- [Isolation levels](#isolation-levels)
- [Outbox integration](#outbox-integration)

## The contract

`IUnitOfWork` lives in `CQRSharp.Persistence` (add `using CQRSharp.Persistence;` to the file that implements it):

```csharp
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }
    Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}
```

| Member | What an implementation must do |
| --- | --- |
| `HasActiveTransaction` | Report the real transaction on the connection, whoever began it, not only the ones begun through this instance. A request that sees `true` takes part in the open transaction, and the `Transactional` outbox mode stores a notification only while it is `true`. |
| `BeginTransactionAsync(level, ct)` | Begin a transaction. `IsolationLevel.Unspecified` asks for the data store's own default. Throw `InvalidOperationException` when a transaction is already active. |
| `CommitAsync(ct)` | Persist every pending change (an ORM's tracked changes included), then commit. The pipeline calls nothing else to save, so a commit that does not persist pending changes loses them. |
| `RollbackAsync(ct)` | Roll back **and discard every pending change** (an ORM's change tracker included), so the next attempt or the next request in the scope starts clean. With no active transaction, still discard the pending changes, and do not throw: it is also called after a failed commit. |

An implementation needs no database transaction to be valid. Over an ORM change tracker alone, `BeginTransactionAsync`
can note that a unit of work is open, `CommitAsync` save the tracked changes and `RollbackAsync` clear them.

The unit of work is registered scoped, so every request in a scope shares one instance, and a retry reuses it: that is
why a rollback must leave nothing behind. Savepoints are not part of the contract; a nested transactional request takes
part in its caller's transaction and cannot roll back only its own part.

## Enabling the behavior

Register the unit of work and the behavior with `UseUnitOfWork<TUnitOfWork>`, supplying a factory (a delegate, so no
reflection is involved and it works under Native AOT):

```csharp
services.AddCqrsGenerated(b => b
    .UseUnitOfWork<AppUnitOfWork>(
        sp => new AppUnitOfWork(sp.GetRequiredService<AppDbConnection>()),
        o => o.DefaultIsolationLevel = IsolationLevel.ReadCommitted));
```

`UnitOfWorkOptions` (namespace `CQRSharp.Pipelines`):

| Option | Default | Meaning |
| --- | --- | --- |
| `DefaultIsolationLevel` | `Unspecified` | The level a transaction begins with when its request names none: `Unspecified` is the data store's own default. Must be `Unspecified` or a defined level; host start fails otherwise. |
| `RollbackOnFailedResult` | `true` | Whether a command that *returns* a failed `CommandResult` is rolled back like one that threw; see [What counts as failure](#what-counts-as-failure). |

Calling `UseUnitOfWork` again replaces the factory, and its `configure` callbacks run in call order.

## The Entity Framework Core unit of work

The EF Core package ships the unit of work over a `DbContext`, `EfCoreUnitOfWork<TContext>` (namespace
`CQRSharp.EntityFrameworkCore`). Register it with one verb, after registering the context yourself:

```csharp
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
services.AddCqrsGenerated(b => b.UseEntityFrameworkCoreUnitOfWork<AppDbContext>());
```

`UseEntityFrameworkCoreUnitOfWork<TContext>(configure?)` is
`UseUnitOfWork(sp => new EfCoreUnitOfWork<TContext>(sp.GetRequiredService<TContext>()), configure)`. The unit of work:

- counts a transaction the application opened on the context itself (`Database.BeginTransactionAsync`) as active, so a
  transactional request takes part in it;
- commits by calling `SaveChangesAsync` and then committing the transaction;
- rolls back the transaction and clears the context's **whole** change tracker, not only what the failed request
  changed;
- needs a relational provider for any isolation level other than `Unspecified`;
- refuses to begin under a retrying execution strategy (`EnableRetryOnFailure`), which rejects a transaction the
  application begins itself. Retry whole requests with [`UseResilience`](idempotency-and-resilience.md#resilience--retries),
  which runs outside the unit of work.

Handlers get the same scoped `DbContext` from DI. Over that context, the EF Core outbox store and inbox join the
transaction; see [Integrations](integrations.md#entity-framework-core).

## Marking transactional requests

The behavior wraps only requests that opt in through a marker; everything else passes through untouched:

```csharp
// A command: committed when it succeeds.
public sealed class TransferFunds : CommandBase, ITransactionalCommand
{
    public IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    public required decimal Amount { get; init; }
}

// A value-returning command opts in the same way.
public sealed class OpenAccount : ResultCommandBase<string>, ITransactionalCommand
{
    public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;   // the configured default
}

// A query or stream: a consistent read, rolled back when it completes.
public sealed class GetStatement : QueryBase<Statement>, ITransactionalQuery
{
    public IsolationLevel IsolationLevel => IsolationLevel.Snapshot;
    public bool IsReadOnly => true;
}
```

- `ITransactionalCommand` is for commands only, of either result shape, and carries an `IsolationLevel`. It opts a
  command into a transaction; it does not make a type a command, so derive from `CommandBase` or `ResultCommandBase<T>`
  (or implement `ICommand` / `ICommand<TResult>`) as well. On a query or a stream it would still commit, but the request
  keeps its query or stream lifecycle notifications, span and metrics; the **CQRA015** analyzer warns about it. A query
  or stream that writes uses `ITransactionalQuery` with `IsReadOnly => false`.
- `ITransactionalQuery` carries an `IsolationLevel` and `IsReadOnly`. With `IsReadOnly => true` (the usual case) the
  transaction only serves a consistent read and is rolled back when the query completes. With `false` the query writes,
  and its unit of work commits like a command's, notifications included. It applies to queries and streaming requests.
- Both properties are get-only on the interfaces. An expression-bodied property, as above, keeps a model binder or a
  JSON body from setting the isolation level.

`IsolationLevel` is `System.Data.IsolationLevel`.

A transactional stream runs in one transaction for its whole enumeration. Enumerated to its end, it ends like a query: a
read-only stream (`IsReadOnly => true`) is rolled back, and a writing one (`IsReadOnly => false`) is committed. It is
rolled back, its notifications discarded, when it faults or its consumer stops early.

## What counts as failure

A transactional request is rolled back when it **throws**, when it is canceled, and when a command **returns** a failed
`CommandResult` (`IsSuccess == false`, e.g. `CommandResult.FromError("insufficient funds")`). A command that reports
failure should not commit its writes or announce events for work it says did not happen, so the transaction is rolled
back, the notifications it published are discarded, and the failed result is returned to the caller unchanged; no
exception is thrown. A rollback always runs under `CancellationToken.None`, so a canceled caller cannot stop it.

If a handler deliberately persists state *before* returning a failure (recording a failed login attempt, say), opt out:

```csharp
.UseUnitOfWork<AppUnitOfWork>(factory, o => o.RollbackOnFailedResult = false)
```

The failure is then committed with its notifications. An idempotent command keeps its idempotency key in that case, so a
duplicate gets the same failed result back instead of running the committed work again (see
[Idempotency](idempotency-and-resilience.md#what-can-be-replayed)).

Rolling back discards every pending change, so a retry by the resilience behavior, which runs outside the unit of work,
starts after the failed attempt was rolled back: transaction and pending changes.

## Transactions that are already open

A transactional request that finds a transaction active (`HasActiveTransaction`) takes part in it instead of beginning
its own, and whoever began the transaction commits or rolls it back:

- **Nested requests.** A transactional request sent from a handler in the same scope (`ExecutionScopeMode.Current`)
  takes part in its caller's transaction. It cannot roll back only its own part: there are no savepoints.
- **Transactions the application owns.** When the application opens a transaction itself (with `EfCoreUnitOfWork`, one
  begun on the context counts), requests take part in it, and their notifications are settled when each request ends:
  a store that joins the transaction writes into it, and any other store writes at once, before the application
  commits, because CQRSharp does not own that commit.
- **Outbox deliveries.** The outbox processor can run a delivery in a transaction of its own; see
  [The inbox](outbox.md#the-inbox-effectively-once-delivery).

## Isolation levels

The level a request's transaction begins with is, in order:

1. The request's `IsolationLevel`, when it names a concrete level.
2. Otherwise `UnitOfWorkOptions.DefaultIsolationLevel`, when it names one.
3. Otherwise `IsolationLevel.Unspecified`: the data store's own default.

A property left at its default (`0`, which names no level) or set to `Unspecified` counts as unset.

## Outbox integration

A transactional request's notifications are settled with its transaction: stored inside it, just before the commit,
when the outbox store joins the transaction (the EF Core store over the same context), and right after a successful
commit otherwise. A request rolled back because it failed (it threw, was canceled, returned a failed `CommandResult`, or
its stream was abandoned) stores nothing. A read-only query or stream is rolled back although it succeeded, so its
notifications are settled as without a unit of work: stored when the request ends. The full rules, including the window a non-joining store leaves
after the commit, are in [How a publish reaches the store](outbox.md#how-a-publish-reaches-the-store).

The `Transactional` outbox mode needs a unit of work: with no `IUnitOfWork` registered there is never a transaction, so
nothing reaches the outbox, and the startup validator reports **CQRCONF007** as an error.

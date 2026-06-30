# Unit of work &amp; transactions

The unit-of-work (UoW) behavior wraps a request in a database transaction so its writes — and any
outbox messages it produces — commit or roll back together. CQRSharp does not own your data access; you
implement a thin UoW over your ORM (typically EF Core) and the behavior drives it.

- [The contract](#the-contract)
- [Enabling the behavior](#enabling-the-behavior)
- [Marking transactional requests](#marking-transactional-requests)
- [Implicit vs explicit unit of work](#implicit-vs-explicit-unit-of-work)
- [Isolation levels](#isolation-levels)
- [Outbox integration](#outbox-integration)

## The contract

Implement `IUnitOfWork` over your data-access technology:

```csharp
public interface IUnitOfWork : IAsyncDisposable
{
    Task<int> SaveChangesAsync(CancellationToken ct);     // commit
    TService GetService<TService>() where TService : class; // resolve a transactional service (e.g. DbContext)
}
```

- `SaveChangesAsync` commits the work and returns the number of affected entries.
- `GetService<TService>()` returns a service that participates in the current transaction (your
  `DbContext`, a repository, …).
- Disposing the UoW before a commit rolls back.

For full control over transaction boundaries and savepoints, implement `IExplicitUnitOfWork` instead
(see [below](#implicit-vs-explicit-unit-of-work)).

## Enabling the behavior

Register your UoW and the behavior with `UseUnitOfWork<TUoW>`, supplying an **AOT-safe factory** (a
delegate the compiler can see statically — no reflection):

```csharp
services.AddCqrsGenerated(b => b
    .UseUnitOfWork<EfCoreUnitOfWork>(
        sp => new EfCoreUnitOfWork(sp.GetRequiredService<AppDbContext>()),
        configure: o => o.DefaultIsolationLevel = IsolationLevel.ReadCommitted));
```

## Marking transactional requests

The behavior only wraps requests that opt in via a transactional marker — everything else passes
through untouched:

```csharp
// command-side
public sealed class TransferFunds : CommandBase, ITransactionalCommand
{
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Serializable;
    public required decimal Amount { get; init; }
}

// query-side (e.g. for read consistency; rolled back since nothing is saved)
public sealed class GetStatement : QueryBase<Statement>, ITransactionalQuery<Statement>
{
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Snapshot;
    public bool IsReadOnly => true;
}
```

- `ITransactionalCommand : ICommand` carries an `IsolationLevel`.
- `ITransactionalQuery<TResult>` carries an `IsolationLevel` and an `IsReadOnly` flag — a transactional
  query runs inside a transaction for read consistency and is rolled back (no changes are saved).

> `ITransactionalCommand` is the command-side counterpart to `ITransactionalQuery`.

## Implicit vs explicit unit of work

The behavior adapts to which interface your UoW implements:

- **`IUnitOfWork` (implicit).** The behavior runs the handler and then calls `SaveChangesAsync` to
  commit. Rollback happens by *not* committing (disposing without a save). This suits ORMs like EF Core
  whose `SaveChanges` wraps an implicit transaction.
- **`IExplicitUnitOfWork` (explicit).** The behavior delegates full transaction control to your UoW:

  ```csharp
  public interface IExplicitUnitOfWork : IUnitOfWork
  {
      bool HasActiveTransaction { get; }
      Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct);
      Task CommitAsync(CancellationToken ct);
      Task RollbackAsync(CancellationToken ct);
      Task CreateSavepointAsync(string name, CancellationToken ct);
      Task RollbackToSavepointAsync(string name, CancellationToken ct);
      Task ReleaseSavepointAsync(string name, CancellationToken ct);
  }
  ```

  With an explicit UoW the behavior begins a transaction at the chosen isolation level, commits on
  success, and rolls back on an exception. If a transaction is **already active**
  (`HasActiveTransaction`), the behavior **participates** in it rather than nesting — so nested
  transactional requests (within `ExecutionScopeMode.Current`) share one transaction. Savepoints let a
  handler create nested recovery points within a single transaction.

## Isolation levels

The isolation level for a request is resolved in order:

1. The request's `IsolationLevel` (from `ITransactionalCommand` / `ITransactionalQuery`), when it is not
   `IsolationLevel.Unspecified`.
2. Otherwise `UnitOfWorkOptions.DefaultIsolationLevel` (configured via `UseUnitOfWork`'s `configure`
   callback).

```csharp
.UseUnitOfWork<EfCoreUnitOfWork>(factory, o => o.DefaultIsolationLevel = IsolationLevel.ReadCommitted)
```

## Outbox integration

When a transactional request runs with the [outbox](outbox.md) in `Transactional` mode, notifications
published during the handler are buffered and then **drained and stored within the same transaction**,
right before commit — so the outbox messages persist atomically with your data. If the transaction rolls
back, the messages are never stored, and the events are never delivered. This is what makes "change the
data and publish the event" a single, crash-safe operation.

A plain `IUnitOfWork` (not `IExplicitUnitOfWork`) cannot report whether a transaction is active, so
`Transactional` mode can't detect one and every publish degrades to direct in-process dispatch — the
startup validator warns about this as **CQRCONF002**. Use `IExplicitUnitOfWork` (or a non-transactional
outbox mode) when you rely on the transactional outbox.

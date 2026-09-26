using System.Data;
using CQRSharp.Persistence;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     An <see cref="IUnitOfWork" /> that records what the pipeline asks of it, in order, and fails on demand. A commit
///     that throws leaves the transaction active, as a real driver does, so a test can show the rollback is what ends it.
/// </summary>
public sealed class RecordingUnitOfWork(TransactionLog? log = null) : IUnitOfWork
{
    private readonly Queue<Exception> _commitFailures = new();

    /// <summary>The order in which the unit of work, the outbox store and the signal were called.</summary>
    public TransactionLog Log { get; } = log ?? new TransactionLog();

    public List<IsolationLevel> BeganWith { get; } = new();
    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }
    public List<CancellationToken> RollbackTokens { get; } = new();
    public Exception? RollbackFailure { get; set; }

    /// <summary>Settable, so a test can stand for a transaction something else began.</summary>
    public bool HasActiveTransaction { get; set; }

    /// <summary>Makes the next commit throw <paramref name="failure" />; later commits succeed again.</summary>
    public void FailNextCommit(Exception failure) => _commitFailures.Enqueue(failure);

    public Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        if (HasActiveTransaction) throw new InvalidOperationException("A transaction is already active.");
        BeganWith.Add(isolationLevel);
        HasActiveTransaction = true;
        Log.Add("begin");
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        if (!HasActiveTransaction) throw new InvalidOperationException("No transaction is active.");
        if (_commitFailures.TryDequeue(out var failure))
        {
            Log.Add("commit-failed");
            return Task.FromException(failure);
        }

        Commits++;
        HasActiveTransaction = false;
        Log.Add("commit");
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Rollbacks++;
        RollbackTokens.Add(cancellationToken);
        HasActiveTransaction = false;
        Log.Add("rollback");
        return RollbackFailure is null ? Task.CompletedTask : Task.FromException(RollbackFailure);
    }
}

/// <summary>A shared, thread-safe record of the order in which transaction participants were called.</summary>
public sealed class TransactionLog
{
    private readonly List<string> _entries = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (_entries) _entries.Add(entry);
    }
}

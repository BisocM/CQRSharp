using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The in-memory outbox store with a transcript: it records every call the processor makes, in order, and lets a
///     test script the answers the in-memory store never gives on its own — a renewal that rotates the claim's token (as
///     the EF Core store does), a renewal or processed mark that finds the claim lost, a backlog sample; and signals when
///     a processed mark or a deferral has returned.
/// </summary>
internal sealed class ScriptedOutboxStore : IOutboxStore
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<IReadOnlyList<ClaimedOutboxMessage>> _claimed = [];
    private readonly List<OutboxClaim> _released = [];
    private readonly List<(string Call, TaskCompletionSource Done)> _waiters = [];

    // Rotated tokens map to the claim the in-memory store issued; a token a rotation replaced is lost from then on.
    private readonly Dictionary<string, OutboxClaim> _rotated = new();
    private readonly HashSet<string> _retired = [];

    public ScriptedOutboxStore(TimeProvider time, InMemoryOutboxStoreOptions? options = null)
        => Inner = new InMemoryOutboxStore(time, Options.Create(options ?? new InMemoryOutboxStoreOptions()));

    /// <summary>The store underneath, for what it holds.</summary>
    public InMemoryOutboxStore Inner { get; }

    /// <summary>Renewals hand out a claim with a new token, and the one it replaces is lost.</summary>
    public bool RotateTokenOnRenewal { get; set; }

    /// <summary>Renewals find the claim lost (another processor took the message over).</summary>
    public bool LoseClaimOnRenewal { get; set; }

    /// <summary>Makes the next renewal throw this, once.</summary>
    public Exception? RenewalFailure { get; set; }

    /// <summary>Processed marks find the claim lost.</summary>
    public bool LoseClaimOnProcessedMark { get; set; }

    /// <summary>What <see cref="GetBacklogAsync" /> reports; the in-memory store's own measure when unset.</summary>
    public OutboxBacklog? Backlog { get; set; }

    /// <summary>Runs after a poll that claimed something, before the processor gets the batch.</summary>
    public Action<IReadOnlyList<ClaimedOutboxMessage>>? AfterClaim { get; set; }

    /// <summary>Every call, in order: <c>backlog</c>, <c>claim:{count}</c>, <c>renew</c>, <c>processed</c>, <c>increment</c>, …</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate) return _calls.ToArray();
        }
    }

    /// <summary>
    ///     Completes once a call of <paramref name="call" /> (<c>processed</c>, <c>defer</c>, …) has returned, the first one
    ///     from now on, so what it did is in the store by the time a test resumes.
    /// </summary>
    public Task Called(string call)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiters.Add((call, done));
        return done.Task;
    }

    /// <summary>Every batch a poll claimed, in order.</summary>
    public IReadOnlyList<IReadOnlyList<ClaimedOutboxMessage>> Claimed
    {
        get
        {
            lock (_gate) return _claimed.ToArray();
        }
    }

    /// <summary>Every claim handed back through <see cref="ReleaseAsync" />, exactly as the processor presented it.</summary>
    public IReadOnlyList<OutboxClaim> Released
    {
        get
        {
            lock (_gate) return _released.ToArray();
        }
    }

    public bool JoinsUnitOfWork => false;

    public Task StoreAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        Record("store");
        return Inner.StoreAsync(messages, cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        var batch = await Inner.ClaimPendingAsync(batchSize, cancellationToken);
        lock (_gate)
        {
            _calls.Add($"claim:{batch.Count}");
            if (batch.Count > 0) _claimed.Add(batch);
        }

        if (batch.Count > 0) AfterClaim?.Invoke(batch);
        return batch;
    }

    public async Task<OutboxClaim?> RenewAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        Record("renew");
        if (RenewalFailure is { } failure)
        {
            RenewalFailure = null;
            throw failure;
        }

        if (LoseClaimOnRenewal || !TryResolve(claim, out var current)) return null;

        var renewed = await Inner.RenewAsync(current, cancellationToken);
        if (renewed is not { } inner || !RotateTokenOnRenewal) return renewed;

        var rotated = inner with { Token = $"rotated-{Guid.NewGuid():N}" };
        lock (_gate)
        {
            _retired.Add(claim.Token);
            _rotated.Remove(claim.Token);
            _rotated[rotated.Token] = inner;
        }

        return rotated;
    }

    public async Task<bool> MarkAsProcessedAsync(OutboxClaim claim, CancellationToken cancellationToken)
    {
        Record("processed");
        try
        {
            return !LoseClaimOnProcessedMark && TryResolve(claim, out var current) &&
                   await Inner.MarkAsProcessedAsync(current, cancellationToken);
        }
        finally
        {
            Returned("processed");
        }
    }

    public Task<int> IncrementAttemptAsync(OutboxClaim claim, string? error, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        Record("increment");
        return TryResolve(claim, out var current) ? Inner.IncrementAttemptAsync(current, error, nextRetryAt, cancellationToken) : Task.FromResult(0);
    }

    public Task<bool> MarkAsFailedAsync(OutboxClaim claim, string? error, CancellationToken cancellationToken)
    {
        Record("failed");
        return TryResolve(claim, out var current) ? Inner.MarkAsFailedAsync(current, error, cancellationToken) : Task.FromResult(false);
    }

    public async Task<bool> DeferAsync(OutboxClaim claim, DateTime notBefore, string? reason, CancellationToken cancellationToken)
    {
        Record("defer");
        try
        {
            return TryResolve(claim, out var current) && await Inner.DeferAsync(current, notBefore, reason, cancellationToken);
        }
        finally
        {
            Returned("defer");
        }
    }

    public Task ReleaseAsync(IReadOnlyCollection<OutboxClaim> claims, CancellationToken cancellationToken)
    {
        var current = new List<OutboxClaim>();
        lock (_gate)
        {
            _calls.Add($"release:{claims.Count}");
            _released.AddRange(claims);
        }

        foreach (var claim in claims)
            if (TryResolve(claim, out var resolved))
                current.Add(resolved);

        return Inner.ReleaseAsync(current, cancellationToken);
    }

    public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        Record("backlog");
        return Backlog is { } scripted ? Task.FromResult(scripted) : Inner.GetBacklogAsync(cancellationToken);
    }

    public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int limit, CancellationToken cancellationToken) => Inner.GetDeadLettersAsync(limit, cancellationToken);
    public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) => Inner.RequeueAsync(messageId, cancellationToken);
    public Task<int> PurgeDeadLettersAsync(DateTime failedBefore, CancellationToken cancellationToken) => Inner.PurgeDeadLettersAsync(failedBefore, cancellationToken);

    private void Record(string call)
    {
        lock (_gate) _calls.Add(call);
    }

    private void Returned(string call)
    {
        List<TaskCompletionSource> done;
        lock (_gate)
        {
            done = _waiters.Where(w => w.Call == call).Select(w => w.Done).ToList();
            _waiters.RemoveAll(w => w.Call == call);
        }

        foreach (var waiter in done) waiter.TrySetResult();
    }

    // The claim the in-memory store knows for the one the processor presented; false for a token a rotation retired.
    private bool TryResolve(OutboxClaim claim, out OutboxClaim current)
    {
        lock (_gate)
        {
            if (_retired.Contains(claim.Token))
            {
                current = default;
                return false;
            }

            current = _rotated.TryGetValue(claim.Token, out var inner) ? inner : claim;
            return true;
        }
    }
}

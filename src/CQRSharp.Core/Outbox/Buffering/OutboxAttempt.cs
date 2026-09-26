using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     One handler attempt's share of the scope's outbox buffer: what the attempt buffers is attributed to an owner of
///     its own, nested in its request's, so a failed attempt's notifications are discarded without touching what the
///     request, an earlier attempt or anything else in the scope buffered. A retry that succeeds leaves only its own.
/// </summary>
internal readonly struct OutboxAttempt
{
    private readonly ScopedOutbox? _outbox;
    private readonly OutboxOwner? _owner;

    private OutboxAttempt(ScopedOutbox outbox, OutboxOwner owner)
    {
        _outbox = outbox;
        _owner = owner;
    }

    /// <summary>
    ///     Starts an attempt and makes its owner current for the calling async method; a no-op attempt when the outbox is
    ///     off or not registered in <paramref name="services" />.
    /// </summary>
    public static OutboxAttempt Begin(bool outboxEnabled, IServiceProvider services)
        => outboxEnabled && services.GetService<ScopedOutbox>() is { } outbox
            ? new OutboxAttempt(outbox, OutboxOwner.Enter())
            : default;

    /// <summary>
    ///     Makes the attempt's owner current again. Each step of a stream runs in its consumer's flow, where the owner an
    ///     earlier step entered is not current.
    /// </summary>
    public void Resume()
    {
        if (_owner is not null) OutboxOwner.Resume(_owner);
    }

    /// <summary>The attempt failed: the notifications it buffered describe work that did not happen.</summary>
    public void Fail()
    {
        if (_owner is not null) _outbox!.DiscardOwned(_owner);
    }
}

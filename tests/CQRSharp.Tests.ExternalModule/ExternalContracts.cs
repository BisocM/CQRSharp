namespace CQRSharp.Tests.ExternalModule;

/// <summary>An observable side-channel so a test in another assembly can confirm this assembly's handlers ran.</summary>
public static class ExternalSignals
{
    public static int NotificationHandled;
    public static int ExceptionActionRuns;

    public static void Reset()
    {
        NotificationHandled = 0;
        ExceptionActionRuns = 0;
    }
}

/// <summary>A query declared in a separate assembly, dispatched from the composition root to prove cross-assembly routing.</summary>
public sealed class ExternalQuery : QueryBase<string>
{
    public required string Value { get; init; }
}

/// <summary>
///     Deliberately <see langword="internal" />: the composition root cannot reference this type, so it could not
///     register it directly. It is registered because THIS assembly's generated module registers it — the guarantee the
///     per-assembly module design provides.
/// </summary>
internal sealed class ExternalQueryHandler : IQueryHandler<ExternalQuery, string>
{
    public Task<string> Handle(ExternalQuery query, CancellationToken cancellationToken)
        => Task.FromResult($"external:{query.Value}");
}

/// <summary>A notification declared in a separate assembly, to prove cross-assembly notification dispatch.</summary>
public sealed class ExternalNotification : INotification
{
    public required string Message { get; init; }
}

/// <summary>Internal handler for <see cref="ExternalNotification" />; signals via <see cref="ExternalSignals" />.</summary>
internal sealed class ExternalNotificationHandler : INotificationHandler<ExternalNotification>
{
    public Task Handle(ExternalNotification notification, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ExternalSignals.NotificationHandled);
        return Task.CompletedTask;
    }
}

/// <summary>
///     A durable notification declared in a separate assembly: this assembly's module then brings a generated outbox
///     serializer of its own, so the composition root's serializer spans two modules.
/// </summary>
[NotificationName("tests.external.durable")]
public sealed class ExternalDurableNotification : INotification
{
    public required string Message { get; init; }
}

internal sealed class ExternalDurableNotificationHandler : INotificationHandler<ExternalDurableNotification>
{
    public Task Handle(ExternalDurableNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
///     A command this assembly handles that the composition root also handles: proves the host's module wins over a
///     referenced assembly's for a request both declare a handler for.
/// </summary>
public sealed class SharedCommand : CommandBase;

internal sealed class ExternalSharedCommandHandler : ICommandHandler<SharedCommand>
{
    public Task<CommandResult> Handle(SharedCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromError("handled by the referenced assembly"));
}

/// <summary>
///     A query whose handler fails. This assembly declares an exception action for it; the composition root declares the
///     exception handler for the same (request, exception) pair, so the pair's two roles live in two modules.
/// </summary>
public sealed class ExternalFailingQuery : QueryBase<int>;

internal sealed class ExternalFailingQueryHandler : IQueryHandler<ExternalFailingQuery, int>
{
    public Task<int> Handle(ExternalFailingQuery query, CancellationToken cancellationToken)
        => throw new InvalidOperationException("external failure");
}

internal sealed class ExternalFailingQueryAction : IRequestExceptionAction<ExternalFailingQuery, InvalidOperationException>
{
    public Task Execute(ExternalFailingQuery request, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ExternalSignals.ExceptionActionRuns);
        return Task.CompletedTask;
    }
}

/// <summary>The reverse split: this assembly declares the exception handler, the composition root the action.</summary>
public sealed class ExternalRecoveredQuery : QueryBase<int>;

internal sealed class ExternalRecoveredQueryHandler : IQueryHandler<ExternalRecoveredQuery, int>
{
    public Task<int> Handle(ExternalRecoveredQuery query, CancellationToken cancellationToken)
        => throw new InvalidOperationException("external failure");
}

internal sealed class ExternalRecoveredQueryExceptionHandler : IRequestExceptionHandler<ExternalRecoveredQuery, int, InvalidOperationException>
{
    public const int Recovered = 7;

    public Task Handle(
        ExternalRecoveredQuery request,
        InvalidOperationException exception,
        RequestExceptionHandlerState<int> state,
        CancellationToken cancellationToken)
    {
        state.SetHandled(Recovered);
        return Task.CompletedTask;
    }
}

/// <summary>A stream request declared and handled in this assembly, dispatched from the composition root.</summary>
public sealed class ExternalCountdown : StreamRequestBase<int>
{
    public required int From { get; init; }
}

internal sealed class ExternalCountdownHandler : IStreamRequestHandler<ExternalCountdown, int>
{
    public async IAsyncEnumerable<int> Handle(ExternalCountdown request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = request.From; i > 0; i--)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

/// <summary>A custom context type, its factory and the one request that uses it, all in this assembly.</summary>
public sealed class ExternalContext : RequestContextBase
{
    public required string Origin { get; init; }
}

internal sealed class ExternalContextFactory : IRequestContextFactory<ExternalContext>
{
    public ValueTask<ExternalContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new ExternalContext { Origin = "external-factory" });
}

public sealed class ExternalContextQuery : QueryBase<string, ExternalContext>;

internal sealed class ExternalContextQueryHandler : IQueryHandler<ExternalContextQuery, string>
{
    public Task<string> Handle(ExternalContextQuery query, CancellationToken cancellationToken) => Task.FromResult(query.Context!.Origin);
}

using System.Runtime.CompilerServices;

namespace CQRSharp.Tests.Core;

public sealed class ExceptionHookProbe
{
    private int _actionCalls;
    private int _baseHandlerCalls;
    private int _derivedHandlerCalls;
    private int _handledHandlerCalls;

    public int ActionCalls => Volatile.Read(ref _actionCalls);
    public int HandledHandlerCalls => Volatile.Read(ref _handledHandlerCalls);
    public int DerivedHandlerCalls => Volatile.Read(ref _derivedHandlerCalls);
    public int BaseHandlerCalls => Volatile.Read(ref _baseHandlerCalls);

    public void RecordAction() => Interlocked.Increment(ref _actionCalls);
    public void RecordHandledHandler() => Interlocked.Increment(ref _handledHandlerCalls);
    public void RecordDerivedHandler() => Interlocked.Increment(ref _derivedHandlerCalls);
    public void RecordBaseHandler() => Interlocked.Increment(ref _baseHandlerCalls);
}

public sealed class ActionOnlyExceptionCommand : CommandBase;

public sealed class HandledExceptionCommand : CommandBase;

public sealed class DerivedExceptionCommand : CommandBase;

public sealed class ActionOnlyException : Exception;

public sealed class HandledException : Exception;

public class BaseHookException : Exception;

public sealed class DerivedHookException : BaseHookException;

public sealed class ActionOnlyExceptionCommandHandler : ICommandHandler<ActionOnlyExceptionCommand>
{
    public Task<CommandResult> Handle(ActionOnlyExceptionCommand command, CancellationToken cancellationToken)
        => throw new ActionOnlyException();
}

public sealed class HandledExceptionCommandHandler : ICommandHandler<HandledExceptionCommand>
{
    public Task<CommandResult> Handle(HandledExceptionCommand command, CancellationToken cancellationToken)
        => throw new HandledException();
}

public sealed class DerivedExceptionCommandHandler : ICommandHandler<DerivedExceptionCommand>
{
    public Task<CommandResult> Handle(DerivedExceptionCommand command, CancellationToken cancellationToken)
        => throw new DerivedHookException();
}

public sealed class ActionOnlyExceptionAction(ExceptionHookProbe probe)
    : IRequestExceptionAction<ActionOnlyExceptionCommand, ActionOnlyException>
{
    public Task Execute(
        ActionOnlyExceptionCommand request,
        ActionOnlyException exception,
        CancellationToken cancellationToken)
    {
        probe.RecordAction();
        return Task.CompletedTask;
    }
}

public sealed class HandledExceptionHandler(ExceptionHookProbe probe)
    : IRequestExceptionHandler<HandledExceptionCommand, CommandResult, HandledException>
{
    public Task Handle(
        HandledExceptionCommand request,
        HandledException exception,
        RequestExceptionHandlerState<CommandResult> state,
        CancellationToken cancellationToken)
    {
        probe.RecordHandledHandler();
        state.SetHandled(CommandResult.FromError("handled"));
        return Task.CompletedTask;
    }
}

public sealed class DerivedExceptionHandler(ExceptionHookProbe probe)
    : IRequestExceptionHandler<DerivedExceptionCommand, CommandResult, DerivedHookException>
{
    public Task Handle(
        DerivedExceptionCommand request,
        DerivedHookException exception,
        RequestExceptionHandlerState<CommandResult> state,
        CancellationToken cancellationToken)
    {
        probe.RecordDerivedHandler();
        state.SetHandled(CommandResult.FromError("derived"));
        return Task.CompletedTask;
    }
}

public sealed class BaseExceptionHandler(ExceptionHookProbe probe)
    : IRequestExceptionHandler<DerivedExceptionCommand, CommandResult, BaseHookException>
{
    public Task Handle(
        DerivedExceptionCommand request,
        BaseHookException exception,
        RequestExceptionHandlerState<CommandResult> state,
        CancellationToken cancellationToken)
    {
        probe.RecordBaseHandler();
        state.SetHandled(CommandResult.FromError("base"));
        return Task.CompletedTask;
    }
}

public sealed class ActionOnlyExceptionStreamRequest : StreamRequestBase<int>;

public sealed class HandledExceptionStreamRequest : StreamRequestBase<int>;

public sealed class DerivedExceptionStreamRequest : StreamRequestBase<int>;

public sealed class ActionOnlyExceptionStreamRequestHandler : IStreamRequestHandler<ActionOnlyExceptionStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(ActionOnlyExceptionStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 0;
        await Task.Yield();
        throw new ActionOnlyException();
    }
}

public sealed class HandledExceptionStreamRequestHandler : IStreamRequestHandler<HandledExceptionStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(HandledExceptionStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) yield break;
        throw new HandledException();
    }
}

public sealed class DerivedExceptionStreamRequestHandler : IStreamRequestHandler<DerivedExceptionStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(DerivedExceptionStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        throw new DerivedHookException();
    }
}

public sealed class ActionOnlyStreamExceptionAction(ExceptionHookProbe probe)
    : IRequestExceptionAction<ActionOnlyExceptionStreamRequest, ActionOnlyException>
{
    public Task Execute(
        ActionOnlyExceptionStreamRequest request,
        ActionOnlyException exception,
        CancellationToken cancellationToken)
    {
        probe.RecordAction();
        return Task.CompletedTask;
    }
}

public sealed class HandledStreamExceptionHandler(ExceptionHookProbe probe)
    : IRequestExceptionHandler<HandledExceptionStreamRequest, IAsyncEnumerable<int>, HandledException>
{
    public Task Handle(
        HandledExceptionStreamRequest request,
        HandledException exception,
        RequestExceptionHandlerState<IAsyncEnumerable<int>> state,
        CancellationToken cancellationToken)
    {
        probe.RecordHandledHandler();
        state.SetHandled(Fallback());
        return Task.CompletedTask;

        static async IAsyncEnumerable<int> Fallback()
        {
            yield return 42;
            yield return 43;
            await Task.Yield();
        }
    }
}

public sealed class DerivedStreamExceptionHandler(ExceptionHookProbe probe)
    : IRequestExceptionHandler<DerivedExceptionStreamRequest, IAsyncEnumerable<int>, DerivedHookException>
{
    public Task Handle(
        DerivedExceptionStreamRequest request,
        DerivedHookException exception,
        RequestExceptionHandlerState<IAsyncEnumerable<int>> state,
        CancellationToken cancellationToken)
    {
        probe.RecordDerivedHandler();
        state.SetHandled(Empty());
        return Task.CompletedTask;

        static async IAsyncEnumerable<int> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }
}

public sealed class BaseStreamExceptionHandler(ExceptionHookProbe probe)
    : IRequestExceptionHandler<DerivedExceptionStreamRequest, IAsyncEnumerable<int>, BaseHookException>
{
    public Task Handle(
        DerivedExceptionStreamRequest request,
        BaseHookException exception,
        RequestExceptionHandlerState<IAsyncEnumerable<int>> state,
        CancellationToken cancellationToken)
    {
        probe.RecordBaseHandler();
        state.SetHandled(Empty());
        return Task.CompletedTask;

        static async IAsyncEnumerable<int> Empty()
        {
            await Task.Yield();
            yield break;
        }
    }
}

/// <summary>
///     Fails with a cancellation: one its caller asked for when <see cref="Caller" /> is set (the handler cancels it
///     first), otherwise one nobody asked for, as an HttpClient timeout throws.
/// </summary>
public sealed class CancelledDependencyCommand : CommandBase
{
    public CancellationTokenSource? Caller { get; init; }
}

public sealed class CancelledDependencyCommandHandler : ICommandHandler<CancelledDependencyCommand>
{
    public async Task<CommandResult> Handle(CancelledDependencyCommand command, CancellationToken cancellationToken)
    {
        if (command.Caller is { } caller)
        {
            await caller.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        throw new TaskCanceledException("dependency timed out");
    }
}

public sealed class CancelledDependencyFallback(ExceptionHookProbe probe)
    : IRequestExceptionHandler<CancelledDependencyCommand, CommandResult, OperationCanceledException>
{
    public Task Handle(
        CancelledDependencyCommand request,
        OperationCanceledException exception,
        RequestExceptionHandlerState<CommandResult> state,
        CancellationToken cancellationToken)
    {
        probe.RecordHandledHandler();
        state.SetHandled(CommandResult.FromError("fallback"));
        return Task.CompletedTask;
    }
}

/// <summary>The streaming counterpart of <see cref="CancelledDependencyCommand" />, failing after its first item.</summary>
public sealed class CancelledDependencyStream : StreamRequestBase<int>
{
    public CancellationTokenSource? Caller { get; init; }
}

public sealed class CancelledDependencyStreamHandler : IStreamRequestHandler<CancelledDependencyStream, int>
{
    public async IAsyncEnumerable<int> Handle(CancelledDependencyStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        if (request.Caller is { } caller)
        {
            await caller.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        throw new TaskCanceledException("dependency timed out");
    }
}

public sealed class CancelledDependencyStreamFallback(ExceptionHookProbe probe)
    : IRequestExceptionHandler<CancelledDependencyStream, IAsyncEnumerable<int>, OperationCanceledException>
{
    public Task Handle(
        CancelledDependencyStream request,
        OperationCanceledException exception,
        RequestExceptionHandlerState<IAsyncEnumerable<int>> state,
        CancellationToken cancellationToken)
    {
        probe.RecordHandledHandler();
        state.SetHandled(Fallback());
        return Task.CompletedTask;

        static async IAsyncEnumerable<int> Fallback()
        {
            await Task.Yield();
            yield return 99;
        }
    }
}


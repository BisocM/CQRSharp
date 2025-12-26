using CQRSharp.Abstractions.Data.Interfaces.Exceptions;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Abstractions.Data.Models.Exceptions;

namespace CQRSharp.Tests.Shared;

public sealed class ExceptionHookProbe
{
    private int _actionCalls;
    private int _handledHandlerCalls;
    private int _derivedHandlerCalls;
    private int _baseHandlerCalls;

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
    public async IAsyncEnumerable<int> Handle(ActionOnlyExceptionStreamRequest request, CancellationToken cancellationToken)
    {
        yield return 0;
        await Task.Yield();
        throw new ActionOnlyException();
    }
}

public sealed class HandledExceptionStreamRequestHandler : IStreamRequestHandler<HandledExceptionStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(HandledExceptionStreamRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) yield break;
        throw new HandledException();
    }
}

public sealed class DerivedExceptionStreamRequestHandler : IStreamRequestHandler<DerivedExceptionStreamRequest, int>
{
    public async IAsyncEnumerable<int> Handle(DerivedExceptionStreamRequest request, CancellationToken cancellationToken)
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

namespace CQRSharp.Tests.Core;

// Requests and a post-handler attribute that tests of more than one feature (lifecycle, outbox flushing, closed
// behaviors, queued and widened dispatch) share.

public sealed class ThrowingPostHandlerAttribute : Attribute, IPostHandlerAttribute
{
    public int PostHandlerExecutionPriority => 0;

    public Task OnAfterHandle(IRequest request, RequestOutcome outcome, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        => throw new InvalidOperationException("post-handler boom");
}

public sealed class NamingQuery : QueryBase<string>;

public sealed class NamingQueryHandler : IQueryHandler<NamingQuery, string>
{
    public Task<string> Handle(NamingQuery query, CancellationToken cancellationToken) => Task.FromResult("name");
}

public sealed class QueuedInnerCommand : CommandBase;

public sealed class QueuedInnerCommandHandler : ICommandHandler<QueuedInnerCommand>
{
    public Task<CommandResult> Handle(QueuedInnerCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}

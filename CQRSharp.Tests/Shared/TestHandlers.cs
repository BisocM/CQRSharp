using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;

namespace CQRSharp.Tests.Shared;

/// <summary>
/// A test implementation of a command handler for <see cref="TestCommand"/>.
/// </summary>
public class TestCommandHandler : ICommandHandler<TestCommand>
{
    /// <inheritdoc />
    public virtual Task<CommandResult> Handle(TestCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>
/// A test implementation of a query handler for <see cref="TestQuery"/>.
/// </summary>
public class TestQueryHandler : IQueryHandler<TestQuery, TestQueryResult>
{
    /// <inheritdoc />
    public virtual Task<TestQueryResult> Handle(TestQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new TestQueryResult("Success"));
}
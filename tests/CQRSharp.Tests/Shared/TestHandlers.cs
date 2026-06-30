using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Models.Commands;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A test implementation of a command handler for <see cref="TestCommand" />.
/// </summary>
public class TestCommandHandler : ICommandHandler<TestCommand>
{
    /// <inheritdoc />
    public virtual Task<CommandResult> Handle(TestCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>
///     A test implementation of a query handler for <see cref="TestQuery" />.
/// </summary>
public class TestQueryHandler : IQueryHandler<TestQuery, TestQueryResult>
{
    /// <inheritdoc />
    public virtual Task<TestQueryResult> Handle(TestQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new TestQueryResult("Success"));
}

/// <summary>
///     A test implementation of a command handler for <see cref="TransactionalCommand" />.
/// </summary>
public class TransactionalCommandHandler : ICommandHandler<TransactionalCommand>
{
    /// <inheritdoc />
    public virtual Task<CommandResult> Handle(TransactionalCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>
///     A test implementation of a command handler for <see cref="NonTransactionalCommand" />.
/// </summary>
public class NonTransactionalCommandHandler : ICommandHandler<NonTransactionalCommand>
{
    /// <inheritdoc />
    public virtual Task<CommandResult> Handle(NonTransactionalCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>
///     A test implementation of a command handler for <see cref="IdempotentTestCommand" />.
/// </summary>
public class IdempotentTestCommandHandler : ICommandHandler<IdempotentTestCommand>
{
    /// <inheritdoc />
    public virtual Task<CommandResult> Handle(IdempotentTestCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

/// <summary>
///     A test implementation of a command handler for <see cref="RetryableTestCommand" />.
/// </summary>
public class RetryableTestCommandHandler : ICommandHandler<RetryableTestCommand>
{
    /// <inheritdoc />
    public virtual Task<CommandResult> Handle(RetryableTestCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}
using System.Data;
using CQRSharp.Pipelines;

namespace CQRSharp.Tests.Shared;

/// <summary>
///     A basic command for general testing purposes.
/// </summary>
public class TestCommand : CommandBase;

/// <summary>
///     A basic query for general testing purposes.
/// </summary>
public class TestQuery : QueryBase<TestQueryResult>;

/// <summary>
///     Represents the result of a <see cref="TestQuery" />.
/// </summary>
public record TestQueryResult(string Value);

/// <summary>
///     A command implementing <see cref="ITransactionalCommand" /> for testing unit of work behavior.
/// </summary>
public class TransactionalCommand : CommandBase, ITransactionalCommand
{
    /// <inheritdoc />
    /// <remarks>Left unset by default (0, no member of the enum), which must fall back to the configured default.</remarks>
    public IsolationLevel IsolationLevel { get; init; }
}

/// <summary>
///     A command that does not implement <see cref="ITransactionalCommand" /> for testing unit of work bypass logic.
/// </summary>
public class NonTransactionalCommand : CommandBase;

/// <summary>
///     A command implementing <see cref="IIdempotentRequest" />, used to exercise idempotency wiring (and the
///     CQRCONF005 startup check when no idempotency behavior is registered).
/// </summary>
public class IdempotentTestCommand : CommandBase, IIdempotentRequest
{
    /// <inheritdoc />
    public string IdempotencyKey => "idempotent-test-command";
}

/// <summary>
///     A command implementing <see cref="IRetryableRequest" />, used to exercise resilience wiring (and the
///     CQRCONF006 startup check when no resilience behavior is registered).
/// </summary>
public class RetryableTestCommand : CommandBase, IRetryableRequest;

/// <summary>
///     A command that requires a rate-limited context, used for testing rate-limiting behavior.
/// </summary>
public class TestRateLimitedCommand : RequestBase<IRateLimitedContext>;

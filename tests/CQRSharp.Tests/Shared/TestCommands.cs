using System.Data;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Transactions;
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
///     A command implementing <see cref="ITransactionalRequest" /> for testing unit of work behavior.
/// </summary>
public class TransactionalCommand : CommandBase, ITransactionalRequest
{
    /// <inheritdoc />
    public IsolationLevel IsolationLevel { get; set; }
}

/// <summary>
///     A command that does not implement <see cref="ITransactionalRequest" /> for testing unit of work bypass logic.
/// </summary>
public class NonTransactionalCommand : CommandBase;

/// <summary>
///     A command that requires a rate-limited context, used for testing rate-limiting behavior.
/// </summary>
public class TestRateLimitedCommand : RequestBase<IRateLimitedContext>;

/// <summary>
///     Another distinct command that requires a rate-limited context, used for testing rate-limiting scopes.
/// </summary>
public class OtherRateLimitedCommand : RequestBase<IRateLimitedContext>;
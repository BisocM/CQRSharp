using System.Data;

namespace CQRSharp;

/// <summary>
///     Marks a query (or a streaming request) as running in a unit-of-work transaction, typically for a consistent read.
///     Not tied to a result type; the command-side counterpart is <see cref="ITransactionalCommand" />.
/// </summary>
public interface ITransactionalQuery
{
    /// <summary>
    ///     The isolation level for the transaction. Left at its default (or set to <see cref="IsolationLevel.Unspecified" />),
    ///     the unit of work's configured default applies, and without one the data store's own default.
    /// </summary>
    IsolationLevel IsolationLevel { get; }

    /// <summary>Whether the query only reads.</summary>
    /// <remarks>
    ///     <c>true</c> (the usual case) means the transaction only serves a consistent read and is rolled back when the
    ///     query completes; <c>false</c> means the query writes, and its unit of work is committed like a command's.
    /// </remarks>
    bool IsReadOnly { get; }
}

namespace CQRSharp.Abstractions.Attributes.Pipelines;

/// <summary>
///     The outcome of a request after its handler ran: the value it returned, or the exception it threw. Handed to an
///     outcome-aware post-handler (<see cref="IPostHandlerOutcomeAware" />) so a cross-cutting concern such as auditing
///     can classify on what actually happened — including a value that encodes a business denial on an otherwise
///     successful dispatch (for example, a login that returns a "bad credentials" verdict without throwing).
/// </summary>
public readonly struct RequestOutcome
{
    private RequestOutcome(object? result, Exception? exception)
    {
        Result = result;
        Exception = exception;
    }

    /// <summary>
    ///     The value the handler returned — the dispatch result, e.g. a <c>CommandResult&lt;T&gt;</c> or a query value —
    ///     or <c>null</c> if the handler threw. Cast it to the request's result type to read the business outcome.
    /// </summary>
    public object? Result { get; }

    /// <summary>The exception the handler (or pipeline) threw, or <c>null</c> if it returned normally.</summary>
    public Exception? Exception { get; }

    /// <summary>
    ///     <c>true</c> when the handler threw — i.e. the dispatch faulted. This is the <em>dispatch</em> outcome, not a
    ///     business verdict: a request can return normally (<c>Threw == false</c>) with a <see cref="Result" /> that
    ///     nonetheless encodes a failure. Read <see cref="Result" /> for the latter.
    /// </summary>
    public bool Threw => Exception is not null;

    /// <summary>Creates an outcome for a handler that returned <paramref name="result" /> without throwing.</summary>
    public static RequestOutcome FromResult(object? result) => new(result, null);

    /// <summary>Creates an outcome for a handler that threw <paramref name="exception" />.</summary>
    public static RequestOutcome FromException(Exception exception) => new(null, exception);
}

namespace CQRSharp;

/// <summary>
///     The outcome of a <em>value-returning</em> command (one declared with <c>ICommand&lt;TResult&gt;</c>): a
///     <see cref="CommandResult" /> that also carries a <typeparamref name="TResult" /> on success.
/// </summary>
/// <remarks>
///     Intended for a value that no query could ever reproduce — a server-side secret minted at the instant of the
///     operation and never persisted in readable form (a one-time API key, a generated token shown exactly once). For
///     anything a query can return, use a plain <see cref="CommandResult" /> and read the value via a query.
/// </remarks>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public sealed record CommandResult<TResult> : CommandResult
{
    /// <summary>
    ///     Rebuilds a result from its parts. Prefer <see cref="FromSuccess" /> / <see cref="FromError" /> in handlers; this
    ///     exists so a serializer — System.Text.Json source generation included, which cannot use a non-public
    ///     constructor — can round-trip a result, e.g. to replay it to a duplicate idempotent request.
    /// </summary>
    /// <param name="isSuccess">Whether the command succeeded.</param>
    /// <param name="value">The produced value; meaningful only on success.</param>
    /// <param name="errorMessage">The error message on failure.</param>
    /// <param name="errorCode">An optional error code on failure.</param>
    public CommandResult(bool isSuccess, TResult? value, string? errorMessage, int? errorCode)
        : base(isSuccess, errorMessage, errorCode)
        => Value = value;

    /// <summary>The value produced on success; <see langword="default" /> when the command failed.</summary>
    public TResult? Value { get; }

    /// <summary>Creates a successful result carrying <paramref name="value" />.</summary>
    public static CommandResult<TResult> FromSuccess(TResult value) => new(true, value, null, null);

    /// <summary>Creates a failed result (carrying no value).</summary>
    public static new CommandResult<TResult> FromError(string errorMessage, int? errorCode = null)
        => new(false, default, errorMessage, errorCode);

    /// <inheritdoc />
    // Delegate to the base ToString so the carried Value is NEVER printed — it may be a secret.
    public override string ToString() => base.ToString();
}

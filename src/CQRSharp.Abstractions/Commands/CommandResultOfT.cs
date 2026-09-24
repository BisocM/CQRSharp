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
    ///     Rebuilds a result from its parts. Prefer the factories (<see cref="FromSuccess" />, <see cref="FromError(string, int?)" />,
    ///     <see cref="NotFound" />, <see cref="Invalid(ValidationFailure[])" />, …) in handlers; this exists so a
    ///     serializer — System.Text.Json source generation included, which cannot use a non-public constructor — can
    ///     round-trip a result, e.g. to replay it to a duplicate idempotent request.
    /// </summary>
    /// <param name="isSuccess">Whether the command succeeded.</param>
    /// <param name="value">The produced value; meaningful only on success.</param>
    /// <param name="errorKind">The kind of failure; <see cref="CommandErrorKind.None" /> on success.</param>
    /// <param name="errorMessage">The error message on failure.</param>
    /// <param name="errorCode">An optional error code on failure.</param>
    /// <param name="validationFailures">The validation failures of a validation failure; null for none.</param>
    public CommandResult(
        bool isSuccess,
        TResult? value,
        CommandErrorKind errorKind,
        string? errorMessage,
        int? errorCode,
        IReadOnlyList<ValidationFailure>? validationFailures)
        : base(isSuccess, errorKind, errorMessage, errorCode, validationFailures)
        => Value = isSuccess ? value : default;

    /// <summary>The value produced on success; <see langword="default" /> when the command failed.</summary>
    public TResult? Value { get; }

    /// <summary>Creates a successful result carrying <paramref name="value" />.</summary>
    /// <param name="value">The produced value.</param>
    /// <returns>A success result.</returns>
    public static CommandResult<TResult> FromSuccess(TResult value) => new(true, value, CommandErrorKind.None, null, null, null);

    /// <summary>Creates a failed result (carrying no value) with the <see cref="CommandErrorKind.Failure" /> kind.</summary>
    /// <param name="errorMessage">A description of why the command failed.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static new CommandResult<TResult> FromError(string errorMessage, int? errorCode = null)
        => new(false, default, CommandErrorKind.Failure, errorMessage, errorCode, null);

    /// <summary>Creates a failed result (carrying no value) for the given kind of reason.</summary>
    /// <param name="errorKind">The kind of failure; <see cref="CommandErrorKind.None" /> is not a failure.</param>
    /// <param name="errorMessage">A description of why the command failed.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="errorKind" /> is <see cref="CommandErrorKind.None" />.</exception>
    public static new CommandResult<TResult> FromError(CommandErrorKind errorKind, string errorMessage, int? errorCode = null)
    {
        EnsureFailureKind(errorKind);
        return new CommandResult<TResult>(false, default, errorKind, errorMessage, errorCode, null);
    }

    /// <summary>Creates a <see cref="CommandErrorKind.NotFound" /> failure.</summary>
    /// <param name="errorMessage">A description of what was not found.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static new CommandResult<TResult> NotFound(string errorMessage, int? errorCode = null)
        => new(false, default, CommandErrorKind.NotFound, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Conflict" /> failure.</summary>
    /// <param name="errorMessage">A description of the conflict.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static new CommandResult<TResult> Conflict(string errorMessage, int? errorCode = null)
        => new(false, default, CommandErrorKind.Conflict, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Unauthorized" /> failure.</summary>
    /// <param name="errorMessage">A description for the caller.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static new CommandResult<TResult> Unauthorized(string errorMessage, int? errorCode = null)
        => new(false, default, CommandErrorKind.Unauthorized, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Forbidden" /> failure.</summary>
    /// <param name="errorMessage">A description for the caller.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static new CommandResult<TResult> Forbidden(string errorMessage, int? errorCode = null)
        => new(false, default, CommandErrorKind.Forbidden, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Unavailable" /> failure.</summary>
    /// <param name="errorMessage">A description of what is unavailable.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static new CommandResult<TResult> Unavailable(string errorMessage, int? errorCode = null)
        => new(false, default, CommandErrorKind.Unavailable, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Validation" /> failure carrying the given failures as data.</summary>
    /// <param name="failures">What is wrong with the input.</param>
    /// <returns>A failure result whose <see cref="CommandResult.ErrorMessage" /> is a fixed summary.</returns>
    public static new CommandResult<TResult> Invalid(params ValidationFailure[] failures)
        => Invalid((IReadOnlyList<ValidationFailure>)(failures ?? throw new ArgumentNullException(nameof(failures))), null);

    /// <summary>Creates a <see cref="CommandErrorKind.Validation" /> failure carrying the given failures as data.</summary>
    /// <param name="failures">What is wrong with the input.</param>
    /// <param name="errorMessage">An optional summary for the caller; defaults to a fixed one.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="failures" /> is <see langword="null" />.</exception>
    public static new CommandResult<TResult> Invalid(IReadOnlyList<ValidationFailure> failures, string? errorMessage = null, int? errorCode = null)
    {
        if (failures is null) throw new ArgumentNullException(nameof(failures));
        return new CommandResult<TResult>(false, default, CommandErrorKind.Validation, ValidationSummary(errorMessage), errorCode, failures);
    }

    /// <inheritdoc />
    // Delegate to the base ToString so the carried Value is NEVER printed — it may be a secret.
    public override string ToString() => base.ToString();
}

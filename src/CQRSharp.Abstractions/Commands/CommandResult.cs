namespace CQRSharp;

/// <summary>
///     Represents the result of a command execution.
/// </summary>
/// <remarks>
///     <para>
///         Commands typically represent an intent to modify the system state, which might not
///         return a meaningful value beyond success or failure. This record is used to unify
///         the representation of the outcome, providing a stable interface for middleware or
///         other handlers to inspect.
///     </para>
///     <para>
///         A failure carries a <see cref="ErrorKind" /> that says what kind of failure it is, a message for the caller,
///         an optional application-defined <see cref="ErrorCode" />, and — for a <see cref="CommandErrorKind.Validation" />
///         failure — the <see cref="ValidationFailures" /> as data, so validation performed inside a handler is reported
///         the same way as validation performed by the pipeline.
///     </para>
/// </remarks>
public record CommandResult
{
    private const string DefaultValidationMessage = "Validation failed.";

    /// <summary>
    ///     Initializes a new instance of the <see cref="CommandResult" /> record from every part of the outcome.
    /// </summary>
    /// <param name="isSuccess">Indicates whether the command succeeded.</param>
    /// <param name="errorKind">
    ///     The kind of failure. Normalized: a success always has <see cref="CommandErrorKind.None" />, and a failure
    ///     given <see cref="CommandErrorKind.None" /> becomes <see cref="CommandErrorKind.Failure" />.
    /// </param>
    /// <param name="errorMessage">Optional error message if the command failed.</param>
    /// <param name="errorCode">Optional error code if the command failed.</param>
    /// <param name="validationFailures">The validation failures of a <see cref="CommandErrorKind.Validation" /> failure; null for none.</param>
    /// <remarks>Protected so <see cref="CommandResult{TResult}" /> can chain to it.</remarks>
    protected CommandResult(
        bool isSuccess,
        CommandErrorKind errorKind,
        string? errorMessage,
        int? errorCode,
        IReadOnlyList<ValidationFailure>? validationFailures)
    {
        // Normalized so that what the properties document holds however the result was built (a deserializer, say):
        // a success carries no error, and only a validation failure carries validation failures.
        IsSuccess = isSuccess;
        ErrorKind = isSuccess
            ? CommandErrorKind.None
            : errorKind == CommandErrorKind.None ? CommandErrorKind.Failure : errorKind;
        ErrorMessage = isSuccess ? null : errorMessage;
        ErrorCode = isSuccess ? null : errorCode;
        ValidationFailures = ErrorKind == CommandErrorKind.Validation && validationFailures is { Count: > 0 }
            ? validationFailures
            : Array.Empty<ValidationFailure>();
    }

    /// <summary>
    ///     Gets a value indicating whether the command succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    ///     Gets the kind of failure: <see cref="CommandErrorKind.None" /> on success, never <c>None</c> on failure.
    /// </summary>
    public CommandErrorKind ErrorKind { get; }

    /// <summary>
    ///     Gets a message describing the error, if any.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    ///     Gets a numeric, application-defined code representing the error, if applicable. CQRSharp never interprets it.
    /// </summary>
    public int? ErrorCode { get; }

    /// <summary>
    ///     Gets the validation failures of a <see cref="CommandErrorKind.Validation" /> failure; empty for every other
    ///     outcome.
    /// </summary>
    public IReadOnlyList<ValidationFailure> ValidationFailures { get; }

    /// <summary>
    ///     Creates a <see cref="CommandResult" /> indicating that the command was successful.
    /// </summary>
    /// <returns>A success result.</returns>
    public static CommandResult FromSuccess()
    {
        return new CommandResult(true, CommandErrorKind.None, null, null, null);
    }

    /// <summary>
    ///     Creates a <see cref="CommandResult" /> indicating that the command failed, with the
    ///     <see cref="CommandErrorKind.Failure" /> kind.
    /// </summary>
    /// <param name="errorMessage">A description of why the command failed.</param>
    /// <param name="errorCode">An optional code describing the nature of the error.</param>
    /// <returns>A failure result.</returns>
    public static CommandResult FromError(string errorMessage, int? errorCode = null)
    {
        return new CommandResult(false, CommandErrorKind.Failure, errorMessage, errorCode, null);
    }

    /// <summary>
    ///     Creates a <see cref="CommandResult" /> indicating that the command failed for the given kind of reason.
    /// </summary>
    /// <param name="errorKind">The kind of failure; <see cref="CommandErrorKind.None" /> is not a failure.</param>
    /// <param name="errorMessage">A description of why the command failed.</param>
    /// <param name="errorCode">An optional code describing the nature of the error.</param>
    /// <returns>A failure result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="errorKind" /> is <see cref="CommandErrorKind.None" />.</exception>
    public static CommandResult FromError(CommandErrorKind errorKind, string errorMessage, int? errorCode = null)
    {
        EnsureFailureKind(errorKind);
        return new CommandResult(false, errorKind, errorMessage, errorCode, null);
    }

    /// <summary>Creates a <see cref="CommandErrorKind.NotFound" /> failure.</summary>
    /// <param name="errorMessage">A description of what was not found.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static CommandResult NotFound(string errorMessage, int? errorCode = null)
        => new(false, CommandErrorKind.NotFound, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Conflict" /> failure.</summary>
    /// <param name="errorMessage">A description of the conflict.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static CommandResult Conflict(string errorMessage, int? errorCode = null)
        => new(false, CommandErrorKind.Conflict, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Unauthorized" /> failure.</summary>
    /// <param name="errorMessage">A description for the caller.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static CommandResult Unauthorized(string errorMessage, int? errorCode = null)
        => new(false, CommandErrorKind.Unauthorized, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Forbidden" /> failure.</summary>
    /// <param name="errorMessage">A description for the caller.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static CommandResult Forbidden(string errorMessage, int? errorCode = null)
        => new(false, CommandErrorKind.Forbidden, errorMessage, errorCode, null);

    /// <summary>Creates a <see cref="CommandErrorKind.Unavailable" /> failure.</summary>
    /// <param name="errorMessage">A description of what is unavailable.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    public static CommandResult Unavailable(string errorMessage, int? errorCode = null)
        => new(false, CommandErrorKind.Unavailable, errorMessage, errorCode, null);

    /// <summary>
    ///     Creates a <see cref="CommandErrorKind.Validation" /> failure carrying the given failures as data.
    /// </summary>
    /// <param name="failures">What is wrong with the input.</param>
    /// <returns>A failure result whose <see cref="ErrorMessage" /> is a fixed summary.</returns>
    public static CommandResult Invalid(params ValidationFailure[] failures)
        => Invalid((IReadOnlyList<ValidationFailure>)(failures ?? throw new ArgumentNullException(nameof(failures))), null);

    /// <summary>
    ///     Creates a <see cref="CommandErrorKind.Validation" /> failure carrying the given failures as data.
    /// </summary>
    /// <param name="failures">What is wrong with the input.</param>
    /// <param name="errorMessage">An optional summary for the caller; defaults to a fixed one.</param>
    /// <param name="errorCode">An optional application-defined code.</param>
    /// <returns>A failure result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="failures" /> is <see langword="null" />.</exception>
    public static CommandResult Invalid(IReadOnlyList<ValidationFailure> failures, string? errorMessage = null, int? errorCode = null)
    {
        if (failures is null) throw new ArgumentNullException(nameof(failures));
        return new CommandResult(false, CommandErrorKind.Validation, ValidationSummary(errorMessage), errorCode, failures);
    }

    /// <summary>The summary message a validation failure carries when none is given.</summary>
    private protected static string ValidationSummary(string? errorMessage) => errorMessage ?? DefaultValidationMessage;

    /// <summary>Rejects <see cref="CommandErrorKind.None" /> where a failure kind is required.</summary>
    /// <param name="errorKind">The kind to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="errorKind" /> is <see cref="CommandErrorKind.None" />.</exception>
    private protected static void EnsureFailureKind(CommandErrorKind errorKind)
    {
        if (errorKind == CommandErrorKind.None)
            throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, "A failed result needs a failure kind; None describes a success.");
    }

    /// <summary>
    ///     Value equality over every part of the outcome, comparing the validation failures element by element.
    /// </summary>
    /// <param name="other">The result to compare with.</param>
    /// <returns><c>true</c> when both describe the same outcome.</returns>
    public virtual bool Equals(CommandResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return EqualityContract == other.EqualityContract &&
               IsSuccess == other.IsSuccess &&
               ErrorKind == other.ErrorKind &&
               ErrorMessage == other.ErrorMessage &&
               ErrorCode == other.ErrorCode &&
               FailuresEqual(ValidationFailures, other.ValidationFailures);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + EqualityContract.GetHashCode();
            hash = hash * 31 + IsSuccess.GetHashCode();
            hash = hash * 31 + (int)ErrorKind;
            hash = hash * 31 + (ErrorMessage?.GetHashCode() ?? 0);
            hash = hash * 31 + (ErrorCode?.GetHashCode() ?? 0);
            for (var i = 0; i < ValidationFailures.Count; i++)
                hash = hash * 31 + ValidationFailures[i].GetHashCode();
            return hash;
        }
    }

    private static bool FailuresEqual(IReadOnlyList<ValidationFailure> left, IReadOnlyList<ValidationFailure> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!left[i].Equals(right[i]))
                return false;

        return true;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (IsSuccess) return "Command succeeded.";

        var code = ErrorCode is { } errorCode ? $" (Code: {errorCode})" : string.Empty;
        return $"Command failed [{ErrorKind}]: {ErrorMessage}{code}";
    }
}

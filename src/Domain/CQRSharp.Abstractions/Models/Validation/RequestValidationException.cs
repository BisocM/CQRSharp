namespace CQRSharp.Abstractions.Models.Validation;

/// <summary>
///     An exception raised when one or more request validators report failures.
/// </summary>
public sealed class RequestValidationException : Exception
{
    /// <summary>
    ///     Creates a new <see cref="RequestValidationException" /> for the given request type and reported failures.
    /// </summary>
    /// <param name="requestType">The request type that failed validation.</param>
    /// <param name="failures">The set of reported validation failures.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestType" /> or <paramref name="failures" /> is <see langword="null" />.</exception>
    public RequestValidationException(Type requestType, IReadOnlyList<ValidationFailure> failures)
        : base($"Validation failed for request '{requestType?.Name ?? "Unknown"}'.")
    {
        RequestType = requestType ?? throw new ArgumentNullException(nameof(requestType));
        Failures = failures ?? throw new ArgumentNullException(nameof(failures));
    }

    /// <summary>
    ///     The request type that failed validation.
    /// </summary>
    public Type RequestType { get; }

    /// <summary>
    ///     The set of reported validation failures.
    /// </summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }
}


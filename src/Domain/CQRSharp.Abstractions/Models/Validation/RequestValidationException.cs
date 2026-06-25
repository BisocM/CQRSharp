namespace CQRSharp.Abstractions.Models.Validation;

/// <summary>
///     An exception raised when one or more request validators report failures.
/// </summary>
public sealed class RequestValidationException : Exception
{
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


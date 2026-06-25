namespace CQRSharp.Core.Exceptions;

/// <summary>
///     Represents the result of executing request exception hooks.
/// </summary>
public readonly record struct RequestExceptionHandlingOutcome(bool Handled, object? Response)
{
    /// <summary>
    ///     An outcome indicating that no hook handled the exception, so it should propagate.
    /// </summary>
    public static RequestExceptionHandlingOutcome NotHandled { get; } = new(false, null);

    /// <summary>
    ///     Creates an outcome indicating that a hook handled the exception and supplied a substitute response.
    /// </summary>
    /// <param name="response">The response to return in place of the failed request, or <c>null</c>.</param>
    /// <returns>A handled outcome carrying the supplied response.</returns>
    public static RequestExceptionHandlingOutcome HandledWith(object? response) => new(true, response);
}
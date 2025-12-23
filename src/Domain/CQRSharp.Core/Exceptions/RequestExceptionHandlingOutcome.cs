namespace CQRSharp.Core.Exceptions;

/// <summary>
///     Represents the result of executing request exception hooks.
/// </summary>
public readonly record struct RequestExceptionHandlingOutcome(bool Handled, object? Response)
{
    public static RequestExceptionHandlingOutcome NotHandled { get; } = new(false, null);

    public static RequestExceptionHandlingOutcome HandledWith(object? response) => new(true, response);
}


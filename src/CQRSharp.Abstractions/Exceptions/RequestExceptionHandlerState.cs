namespace CQRSharp;

/// <summary>
///     Tracks whether an exception has been handled and optionally stores a response to return to the caller.
/// </summary>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class RequestExceptionHandlerState<TResponse>
{
    /// <summary>
    ///     Gets whether the exception was handled.
    /// </summary>
    public bool Handled { get; private set; }

    /// <summary>
    ///     Gets the handled response (may be null for reference types).
    /// </summary>
    public TResponse? Response { get; private set; }

    /// <summary>
    ///     Marks the exception as handled: the request returns <paramref name="response" /> instead of throwing, and no
    ///     further exception handler runs.
    /// </summary>
    /// <param name="response">The response the caller receives.</param>
    public void SetHandled(TResponse response)
    {
        Handled = true;
        Response = response;
    }
}
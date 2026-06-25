namespace CQRSharp.Abstractions.Models.Exceptions;

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
    ///     Marks the exception as handled and provides a response.
    /// </summary>
    public void SetHandled(TResponse response)
    {
        Handled = true;
        Response = response;
    }
}


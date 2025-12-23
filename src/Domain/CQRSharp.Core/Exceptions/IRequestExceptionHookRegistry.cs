namespace CQRSharp.Core.Exceptions;

/// <summary>
///     Provides AOT-safe request exception hook dispatch by request type.
///     Implementations are expected to be provided by the CQRSharp source generator.
/// </summary>
public interface IRequestExceptionHookRegistry
{
    bool TryGetInvoker(Type requestType, out RequestExceptionHookInvoker invoker);
}

/// <summary>
///     Invokes exception hooks (actions + handlers) for a specific request type.
/// </summary>
public delegate Task<RequestExceptionHandlingOutcome> RequestExceptionHookInvoker(
    IServiceProvider services,
    object request,
    Exception exception,
    CancellationToken cancellationToken);


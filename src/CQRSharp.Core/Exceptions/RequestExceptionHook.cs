using System.ComponentModel;
using CQRSharp.Core.Modules;

namespace CQRSharp.Core.Exceptions;

/// <summary>
///     The exception hooks one module declares for one (request, exception type) pair: an invoker for the pair's actions
///     when the module declares one, and one for its handlers when it declares one. Each invoker resolves every action
///     or handler registered for the pair, whichever module declared it. The module composition merges the pair across
///     modules role by role, keeping one actions invoker and one handlers invoker, so every hook of the pair runs, and
///     runs once: every matching action first, then the handlers from the most derived exception type up, until one
///     handles the exception. The hooks are the discovered ones merged with the application's own registrations (see
///     <see cref="DiscoveredServices" />).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RequestExceptionHook
{
    internal RequestExceptionHook(
        Type requestType,
        Type exceptionType,
        int inheritanceDepth,
        RequestExceptionHookInvoker? actions,
        RequestExceptionHookInvoker? handlers)
    {
        RequestType = requestType ?? throw new ArgumentNullException(nameof(requestType));
        ExceptionType = exceptionType ?? throw new ArgumentNullException(nameof(exceptionType));
        InheritanceDepth = inheritanceDepth;
        Actions = actions;
        Handlers = handlers;
    }

    /// <summary>The request type the hooks are declared for.</summary>
    internal Type RequestType { get; }

    /// <summary>The exception type the hooks are declared for.</summary>
    internal Type ExceptionType { get; }

    /// <summary>The exception type's depth below <see cref="object" />; deeper runs first.</summary>
    internal int InheritanceDepth { get; }

    /// <summary>Runs the actions for the pair, or null.</summary>
    internal RequestExceptionHookInvoker? Actions { get; }

    /// <summary>Runs the handlers for the pair, or null.</summary>
    internal RequestExceptionHookInvoker? Handlers { get; }

    /// <summary>
    ///     The hooks a module declares for requests of type <typeparamref name="TRequest" /> failing with
    ///     <typeparamref name="TException" />.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response the request is dispatched with, which its exception handlers supply.</typeparam>
    /// <typeparam name="TException">The exception type.</typeparam>
    /// <param name="actions">Whether the module declares an <c>IRequestExceptionAction</c> for the pair.</param>
    /// <param name="handlers">Whether the module declares an <c>IRequestExceptionHandler</c> for the pair.</param>
    public static RequestExceptionHook For<TRequest, TResponse, TException>(bool actions, bool handlers)
        where TRequest : IRequest<TResponse>
        where TException : Exception
        => new(
            typeof(TRequest),
            typeof(TException),
            Hooks<TRequest, TResponse, TException>.InheritanceDepth,
            actions ? Hooks<TRequest, TResponse, TException>.Actions : null,
            handlers ? Hooks<TRequest, TResponse, TException>.Handlers : null);

    private static class Hooks<TRequest, TResponse, TException>
        where TRequest : IRequest<TResponse>
        where TException : Exception
    {
        public static readonly int InheritanceDepth = DepthOf(typeof(TException));

        public static readonly RequestExceptionHookInvoker Actions = RunActionsAsync;

        public static readonly RequestExceptionHookInvoker Handlers = RunHandlersAsync;

        private static async Task<RequestExceptionHandlingOutcome> RunActionsAsync(
            IServiceProvider services,
            object request,
            Exception exception,
            CancellationToken cancellationToken)
        {
            foreach (var action in DiscoveredServices.Resolve<IRequestExceptionAction<TRequest, TException>>(services))
                await action.Execute((TRequest)request, (TException)exception, cancellationToken).ConfigureAwait(false);

            return RequestExceptionHandlingOutcome.NotHandled;
        }

        private static async Task<RequestExceptionHandlingOutcome> RunHandlersAsync(
            IServiceProvider services,
            object request,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var state = new RequestExceptionHandlerState<TResponse>();
            foreach (var handler in DiscoveredServices.Resolve<IRequestExceptionHandler<TRequest, TResponse, TException>>(services))
            {
                await handler.Handle((TRequest)request, (TException)exception, state, cancellationToken).ConfigureAwait(false);
                if (state.Handled) return RequestExceptionHandlingOutcome.HandledWith(state.Response);
            }

            return RequestExceptionHandlingOutcome.NotHandled;
        }

        private static int DepthOf(Type type)
        {
            var depth = 0;
            for (var current = type.BaseType; current is not null; current = current.BaseType)
                depth++;
            return depth;
        }
    }
}

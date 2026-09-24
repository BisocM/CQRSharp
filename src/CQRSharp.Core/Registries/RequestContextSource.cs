using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Registries;

/// <summary>
///     Where the contexts of one context type come from: the <see cref="IRequestContextFactory{TContext}" /> registered
///     for it. The executor knows a request's context type only as a <see cref="Type" />; a source-generated module
///     supplies one source per context type its requests use, through <see cref="For{TContext}" />, so the factory is
///     called typed, without reflection. Every module's source for a type is the same object, so which module supplies it
///     never matters.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class RequestContextSource
{
    private protected RequestContextSource()
    {
    }

    /// <summary>The source of the contexts of type <typeparamref name="TContext" />.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    public static RequestContextSource For<TContext>() where TContext : IRequestContext => Source<TContext>.Instance;

    /// <summary>The registered factory, or <see langword="null" /> when none is.</summary>
    internal abstract object? ResolveFactory(IServiceProvider services);

    /// <summary>
    ///     Starts creating the context with the registered factory. <see langword="false" /> when no factory is registered.
    /// </summary>
    internal abstract bool TryCreate(
        IServiceProvider services,
        IRequest request,
        CancellationToken cancellationToken,
        out ValueTask<IRequestContext> context);

    private sealed class Source<TContext> : RequestContextSource where TContext : IRequestContext
    {
        public static readonly Source<TContext> Instance = new();

        internal override object? ResolveFactory(IServiceProvider services) => services.GetService<IRequestContextFactory<TContext>>();

        internal override bool TryCreate(
            IServiceProvider services,
            IRequest request,
            CancellationToken cancellationToken,
            out ValueTask<IRequestContext> context)
        {
            if (services.GetService<IRequestContextFactory<TContext>>() is not { } factory)
            {
                context = default;
                return false;
            }

            var creating = factory.CreateContextAsync(request, cancellationToken);
            context = creating.IsCompletedSuccessfully
                ? new ValueTask<IRequestContext>(NotNull(creating.Result, factory))
                : new ValueTask<IRequestContext>(AwaitCreated(creating, factory));
            return true;
        }

        private static async Task<IRequestContext> AwaitCreated(ValueTask<TContext> creating, IRequestContextFactory<TContext> factory)
            => NotNull(await creating.ConfigureAwait(false), factory);

        private static IRequestContext NotNull(TContext? context, IRequestContextFactory<TContext> factory)
            => context ?? throw new InvalidOperationException(
                $"The IRequestContextFactory<{typeof(TContext).Name}> '{factory.GetType().FullName}' returned no context.");
    }
}

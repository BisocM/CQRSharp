namespace CQRSharp;

/// <summary>
///     The built-in factory of <see cref="RequestContextBase" /> contexts: each stamped with the application's
///     <see cref="TimeProvider" /> time. The dispatcher recognises it and builds the same context directly, without
///     resolving it, so it only runs when something asks the container for the factory itself.
/// </summary>
internal sealed class DefaultRequestContextFactory(TimeProvider timeProvider) : IRequestContextFactory<RequestContextBase>
{
    public ValueTask<RequestContextBase> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new RequestContextBase(timeProvider.GetUtcNow().UtcDateTime));
}

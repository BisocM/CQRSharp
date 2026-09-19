namespace CQRSharp;

/// <summary>
///     Provides a default implementation of the <see cref="IRequestContextFactory" /> interface.
///     Used to create instances of <see cref="RequestContextBase" /> with default values for request and user IDs
///     when no custom factories are registered.
/// </summary>
public class DefaultRequestContextFactory : IRequestContextFactory
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the factory over the system clock.</summary>
    public DefaultRequestContextFactory() : this(null)
    {
    }

    /// <summary>Creates the factory over the application's clock.</summary>
    /// <param name="timeProvider">The clock contexts are stamped with; the system clock when <c>null</c>.</param>
    public DefaultRequestContextFactory(TimeProvider? timeProvider)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public RequestContextBase CreateContext(IRequest request)
    {
        return new RequestContextBase(_timeProvider.GetUtcNow().UtcDateTime);
    }
}
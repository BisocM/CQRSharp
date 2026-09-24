using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Infrastructure.Identity;

namespace CQRSharp.Sample.Infrastructure.Factories;

/// <summary>
///     Builds the context of every request declared over <see cref="SampleRequestContext" />. The caller comes from the
///     scope's <see cref="CurrentUser" />, never from the request, so a client cannot choose whose rate limit it spends.
///     The source generator registers the factory; it needs no registration of its own.
/// </summary>
public sealed class CustomRequestContextFactory(CurrentUser currentUser) : IRequestContextFactory<SampleRequestContext>
{
    public ValueTask<SampleRequestContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new SampleRequestContext { UserId = currentUser.UserId });
}

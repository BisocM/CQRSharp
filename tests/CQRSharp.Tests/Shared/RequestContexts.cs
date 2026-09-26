namespace CQRSharp.Tests.Shared;

/// <summary>Gives a request its context outside a dispatch, for tests that run a behavior or a handler directly.</summary>
public static class RequestContexts
{
    /// <summary>Sets <paramref name="context" /> on <paramref name="request" />, as a dispatch would, and returns the request.</summary>
    public static TRequest WithContext<TRequest>(this TRequest request, IRequestContext context) where TRequest : IRequest
    {
        request.Context = context;
        return request;
    }
}

using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Factories;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Factories;

public class CustomRequestContextFactory : IRequestContextFactory<SampleRequestContext>
{
    public SampleRequestContext CreateContext(IRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var userId = "STATIC_USER_ID"; // In a real app, this might come from HttpContext, a JWT, etc.
        return new SampleRequestContext(requestId, userId, DateTime.UtcNow);
    }
}
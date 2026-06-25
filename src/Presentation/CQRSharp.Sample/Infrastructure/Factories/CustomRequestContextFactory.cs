using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Core.Factories;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Infrastructure.Factories;

public class CustomRequestContextFactory(SampleUserContext userContext) : IRequestContextFactory<SampleRequestContext>
{
    public SampleRequestContext CreateContext(IRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var userId = userContext.UserId; // In a real app, this might come from HttpContext, a JWT, etc.
        return new SampleRequestContext(requestId, userId, DateTime.UtcNow);
    }
}

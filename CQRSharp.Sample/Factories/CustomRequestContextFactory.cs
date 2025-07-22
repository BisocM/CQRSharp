using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Factories;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Factories;

//This class is REQUIRED for the automatic population of all the contexts for all commands.
public class CustomRequestContextFactory(IServiceProvider serviceProvider)
    : IRequestContextFactory<SampleRequestContext>
{
    public SampleRequestContext CreateContext(IRequest request)
    {
        //Provide fallback if factories aren't registered
        var requestId = Guid.NewGuid().ToString();

        var userId = "STATIC_USER";

        return new SampleRequestContext(requestId, userId, DateTime.UtcNow);
    }
}
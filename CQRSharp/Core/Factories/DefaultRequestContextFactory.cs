using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Factories;

public class DefaultRequestContextFactory(IServiceProvider serviceProvider) : IRequestContextFactory
{
    public IRequestContext CreateContext(IRequest request)
    {
        var userIdentificationFactory = serviceProvider.GetService<IUserIdentificationFactory>();
        var requestIdentificationFactory = serviceProvider.GetService<IRequestIdentificationFactory>();

        //Provide fallback values if factories aren't registered
        var requestId = requestIdentificationFactory != null
            ? requestIdentificationFactory.GetIdentifier(request)
            : "Request ID factory not registered.";

        var userId = userIdentificationFactory != null
            ? userIdentificationFactory.GetIdentifier(request)
            : "User ID factory not registered.";

        return new RequestContextBase(requestId, userId);
    }
}
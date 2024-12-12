using CQRSharp.Core.Factories;
using CQRSharp.Data.Context;
using CQRSharp.Interfaces.Markers.Request;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Sample.Context
{
    public class CustomRequestContextFactory(IServiceProvider serviceProvider) : IRequestContextFactory
    {
        public IRequestContext CreateContext(IRequest request)
        {
            var userIdentificationFactory = serviceProvider.GetService<IUserIdentificationFactory>();
            var requestIdentificationFactory = serviceProvider.GetService<IRequestIdentificationFactory>();

            //Provide fallback if factories aren't registered
            var requestId = requestIdentificationFactory != null
                ? requestIdentificationFactory.GetIdentifier(request)
                : "Request ID factory not registered.";

            var userId = userIdentificationFactory != null
                ? userIdentificationFactory.GetIdentifier(request)
                : "User ID factory not registered.";

            //For demonstration purposes, we hardcode role and IP.
            var userRole = "Admin";
            var sourceIp = "192.168.1.42";

            return new CustomRequestContext(requestId, userId, userRole, sourceIp);
        }
    }
}
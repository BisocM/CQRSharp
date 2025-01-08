using CQRSharp.Core.Factories;
using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.Sample.Context;

namespace CQRSharp.Sample.Factories
{
    public class CustomRequestContextFactory(IServiceProvider serviceProvider) : IRequestContextFactory
    {
        public IRequestContext CreateContext(IRequest request)
        {
            //Provide fallback if factories aren't registered
            var requestId = Guid.NewGuid().ToString();

            var userId = "STATIC_USER";

            //For demonstration purposes, we hardcode role and IP.
            var userRole = "Admin";
            var sourceIp = "192.168.1.42";

            return new CustomRequestContext(requestId, userId, userRole, sourceIp);
        }
    }
}
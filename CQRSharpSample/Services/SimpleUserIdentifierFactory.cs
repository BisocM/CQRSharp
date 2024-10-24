using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.RateLimiting.Handlers;

namespace CQRSharpSample.Services
{
    public class SimpleUserIdentifierFactory : IUserIdentifierFactory
    {
        public string GetIdentifier(RequestBase request)
        {
            return "user1";
        }
    }
}
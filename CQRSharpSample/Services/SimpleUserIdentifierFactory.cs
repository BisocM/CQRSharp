using CQRSharp.Core.Factories;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharpSample.Services
{
    public class SimpleUserIdentifierFactory : IUserIdentificationFactory
    {
        public string GetIdentifier(RequestBase? request)
        {
            return "example.user_1";
        }
    }
}
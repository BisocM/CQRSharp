using CQRSharp.Core.Factories;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharpSample.Services;

public class SimpleRequestIdentificationFactory : IRequestIdentificationFactory
{
    public string GetIdentifier(RequestBase request)
    {
        return new Guid().ToString();
    }
}
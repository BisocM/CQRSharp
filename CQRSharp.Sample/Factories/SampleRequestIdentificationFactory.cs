using CQRSharp.Core.Factories;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Sample.Factories;

public class SampleRequestIdentificationFactory : IRequestIdentificationFactory
{
    public string GetIdentifier(IRequest request)
    {
        // In a real application, generate a unique ID per request (like a GUID).
        return Guid.NewGuid().ToString("N");
    }
}
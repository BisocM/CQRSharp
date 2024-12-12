using CQRSharp.Core.Factories;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Sample.Factories;

public class SampleUserIdentificationFactory : IUserIdentificationFactory
{
    // For demonstration, returns a static user ID. In real usage, you'd get this from e.g. HttpContext.
    public string GetIdentifier(IRequest? request)
    {
        return "StaticUserIdentifier";
    }
}
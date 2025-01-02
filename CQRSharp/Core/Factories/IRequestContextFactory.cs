using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;

namespace CQRSharp.Core.Factories;

/// <summary>
///     Factory for creating IRequestContext instances.
///     Users can provide their own implementations to produce custom contexts globally.
/// </summary>
public interface IRequestContextFactory
{
    IRequestContext CreateContext(IRequest request);
}
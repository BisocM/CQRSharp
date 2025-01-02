using CQRSharp.Interfaces.Context;

namespace CQRSharp.Interfaces.Markers.Request;

/// <summary>
///     Base class for all requests.
/// </summary>
public abstract class RequestBase : IRequest
{
    /// <inheritdoc />
    public IRequestContext? Context { get; set; }
}
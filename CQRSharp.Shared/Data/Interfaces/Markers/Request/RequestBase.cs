using CQRSharp.Shared.Data.Interfaces.Context;
using CQRSharp.Shared.Data.Models.Requests;

namespace CQRSharp.Shared.Data.Interfaces.Markers.Request;

/// <summary>
///     Represents a fundamental base class for handling requests.
/// </summary>
public abstract class RequestBase<TContext> : IRequest where TContext : IRequestContext
{
    /// <summary>
    ///     A context object that stores request-level metadata like RequestId and UserId.
    /// </summary>
    public TContext? Context { get; set; }

    IRequestContext? IRequest.Context
    {
        get => Context;
        set
        {
            if (value != null) Context = (TContext)value;
            else throw new ArgumentNullException(nameof(value), "Context cannot be null.");
        }
    }

    /// <summary>
    ///     Metadata related to the request. Contains runtime-specific data.
    /// </summary>
    public RequestMetadata? Metadata { get; set; }
}
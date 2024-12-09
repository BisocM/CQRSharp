using CQRSharp.Data;

namespace CQRSharp.Interfaces.Markers.Request
{
    /// <summary>
    /// Base class for all requests.
    /// </summary>
    public abstract class RequestBase : IRequest
    {
        /// <summary>
        /// A context object that stores request-level metadata like RequestId and UserId.
        /// </summary>
        public RequestContextBase Context { get; set; }
    }
}
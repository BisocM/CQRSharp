namespace CQRSharp.Interfaces.Markers.Request
{
    /// <summary>
    /// Base class for all requests.
    /// </summary>
    public abstract class RequestBase : IRequest
    {
        /// <summary>
        /// Unique identifier for the request.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();
    }
}
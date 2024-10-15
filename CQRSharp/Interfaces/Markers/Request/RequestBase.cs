namespace CQRSharp.Interfaces.Markers.Request
{
    /// <summary>
    /// Base class for all requests.
    /// </summary>
    public abstract class RequestBase : IRequest
    {
        /// <summary>
        /// Unique identifier for the request.
        /// Gets populated on a per-request basis by the dispatcher.
        /// </summary>
        public Guid? Id { get; internal set; }

        /// <summary>
        /// Unique identifier for the user, used for rate-limiting purposes.
        /// Populated by the library consumer during pipeline execution.
        /// </summary>
        public string? UserIdentifier { get; set; }
    }
}
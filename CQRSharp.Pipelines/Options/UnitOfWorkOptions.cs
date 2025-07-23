using System.Data;

namespace CQRSharp.Pipelines.Options;

/// <summary>
/// Provides configuration options for the Unit of Work behavior.
/// </summary>
public sealed class UnitOfWorkOptions
{
    /// <summary>
    /// Gets or sets the default isolation level for transactions.
    /// This is used if the request does not specify an isolation level.
    /// Defaults to <see cref="IsolationLevel.ReadCommitted"/>.
    /// </summary>
    public IsolationLevel DefaultIsolationLevel { get; set; } = IsolationLevel.ReadCommitted;
}
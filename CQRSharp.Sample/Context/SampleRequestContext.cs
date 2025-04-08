using CQRSharp.Sample.Management.Menu;
using CQRSharp.Shared.Core.Data.Interfaces.Context;

namespace CQRSharp.Sample.Context;

public class SampleRequestContext(string requestId, string userId, DateTime createdAt)
    : IRequestContext
{
    /// <summary>
    ///     The desired menu state after the completion of the command. Remains null if no menu state needed.
    /// </summary>
    public MenuState? NextState { get; set; }

    private string RequestId { get; } = requestId;
    private string UserId { get; } = userId;
    public DateTime CreatedAt { get; } = createdAt;
}
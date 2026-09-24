namespace CQRSharp.Sample.Infrastructure.Identity;

/// <summary>
///     The user the current DI scope acts for. A web host fills it from the authenticated principal; this host's self-test
///     sets it on the scope each scenario runs in, as a worker does for the job it runs.
/// </summary>
public sealed class CurrentUser
{
    public string UserId { get; set; } = string.Empty;
}

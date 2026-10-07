namespace CQRSharp.Transports;

/// <summary>
///     The diagnostic the compiler reports where code uses the notification transport extension point, which ships as
///     experimental in 5.x: it may change shape in a minor release once real transports have used it. The configuration
///     verbs of a transport package (such as <c>UseRabbitMq</c>) are ordinary, stable API and report nothing.
/// </summary>
internal static class TransportExperiment
{
    /// <summary>The diagnostic id; suppress it (<c>&lt;NoWarn&gt;</c>) in a project that implements or calls the extension point.</summary>
    public const string DiagnosticId = "CQREXP001";

    /// <summary>Where the diagnostic is documented.</summary>
    public const string UrlFormat = "https://github.com/BisocM/CQRSharp/blob/Release/docs/diagnostics.md#experimental-apis-cqrexp";
}

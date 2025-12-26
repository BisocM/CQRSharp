namespace CQRSharp.Core.Diagnostics;

public sealed record CqrsBindingIssue(
    CqrsBindingIssueSeverity Severity,
    string Code,
    string Message);


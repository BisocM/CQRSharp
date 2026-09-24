namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     One issue <see cref="ICqrsDiagnostics" /> found: a <c>CQRDIAG</c> issue in a request's binding, or a
///     <c>CQRCONF</c> issue in the configuration. The startup validator logs these, and fails host start on them as its
///     <see cref="CqrsValidationPolicy" /> says.
/// </summary>
/// <param name="Severity">How serious the issue is.</param>
/// <param name="Code">A stable, machine-readable identifier for the issue.</param>
/// <param name="Message">A human-readable description of the issue.</param>
public sealed record CqrsBindingIssue(
    CqrsBindingIssueSeverity Severity,
    string Code,
    string Message);
namespace CQRSharp.Core.Diagnostics;

/// <summary>
///     A single diagnostic issue found while describing a request binding.
/// </summary>
/// <param name="Severity">How serious the issue is.</param>
/// <param name="Code">A stable, machine-readable identifier for the issue.</param>
/// <param name="Message">A human-readable description of the issue.</param>
public sealed record CqrsBindingIssue(
    CqrsBindingIssueSeverity Severity,
    string Code,
    string Message);
namespace CQRSharp.Abstractions.Data.Models.Validation;

/// <summary>
///     Represents a single validation failure for a request.
/// </summary>
/// <param name="Code">A stable error code.</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="MemberName">An optional member/property name the failure applies to.</param>
public readonly record struct ValidationFailure(
    string Code,
    string Message,
    string? MemberName = null
);


namespace CQRSharp;

/// <summary>
///     Classifies why a command failed, so a caller can react to the <em>kind</em> of failure — retry, ask the user to
///     correct the input, answer 404 or 409 — without parsing <see cref="CommandResult.ErrorMessage" /> or agreeing on
///     <see cref="CommandResult.ErrorCode" /> conventions.
/// </summary>
public enum CommandErrorKind
{
    /// <summary>The command succeeded; there is no error.</summary>
    None,

    /// <summary>The command failed for a reason none of the other kinds describes.</summary>
    Failure,

    /// <summary>The input was rejected; <see cref="CommandResult.ValidationFailures" /> says what is wrong with it.</summary>
    Validation,

    /// <summary>Something the command needs does not exist.</summary>
    NotFound,

    /// <summary>The command conflicts with the current state: a duplicate, a stale version, an invalid state transition.</summary>
    Conflict,

    /// <summary>The caller is not authenticated.</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but may not do this.</summary>
    Forbidden,

    /// <summary>A dependency the command needs is temporarily unavailable; the caller may retry later.</summary>
    Unavailable
}

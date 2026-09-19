using Microsoft.AspNetCore.Http;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Maps a <see cref="CommandResult" /> / <see cref="CommandResult{TResult}" /> to a minimal-API
///     <see cref="IResult" />.
/// </summary>
/// <remarks>
///     <para>
///         A successful result becomes <c>204 No Content</c> (no value), <c>200 OK</c> (with a value) or
///         <c>201 Created</c> (the <c>ToCreatedHttpResult</c> overloads). A failed result becomes an RFC 7807
///         <c>application/problem+json</c> response whose <c>detail</c> is the result's
///         <see cref="CommandResult.ErrorMessage" /> and which carries <see cref="CommandResult.ErrorCode" />, when
///         set, as the <c>errorCode</c> extension member.
///     </para>
///     <para>
///         Overload resolution is static: a <see cref="CommandResult{TResult}" /> held in a variable typed as the base
///         <see cref="CommandResult" /> maps to <c>204</c> and its value is not written.
///     </para>
/// </remarks>
public static class CommandResultHttpExtensions
{
    /// <summary>The ProblemDetails extension member that carries <see cref="CommandResult.ErrorCode" />.</summary>
    public const string ErrorCodeExtensionName = "errorCode";

    /// <summary>
    ///     Maps the result to <c>204 No Content</c> on success, or a ProblemDetails response on failure.
    /// </summary>
    /// <param name="result">The command result to map.</param>
    /// <param name="failureStatusCode">The HTTP status code used when the command failed. Defaults to <c>400</c>.</param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public static IResult ToHttpResult(this CommandResult result,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess ? TypedResults.NoContent() : ToProblem(result, failureStatusCode);
    }

    /// <summary>
    ///     Maps the result to <c>200 OK</c> carrying <see cref="CommandResult{TResult}.Value" /> on success, or a
    ///     ProblemDetails response on failure.
    /// </summary>
    /// <typeparam name="TResult">The value produced on success.</typeparam>
    /// <param name="result">The command result to map.</param>
    /// <param name="failureStatusCode">The HTTP status code used when the command failed. Defaults to <c>400</c>.</param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public static IResult ToHttpResult<TResult>(this CommandResult<TResult> result,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result, failureStatusCode);
    }

    /// <summary>
    ///     Maps the result to <c>201 Created</c> with a <c>Location</c> header on success, or a ProblemDetails
    ///     response on failure.
    /// </summary>
    /// <param name="result">The command result to map.</param>
    /// <param name="location">The URI of the created resource, written to the <c>Location</c> header.</param>
    /// <param name="failureStatusCode">The HTTP status code used when the command failed. Defaults to <c>400</c>.</param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> or <paramref name="location" /> is <see langword="null" />.</exception>
    public static IResult ToCreatedHttpResult(this CommandResult result, string location,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(location);

        return result.IsSuccess ? TypedResults.Created(location) : ToProblem(result, failureStatusCode);
    }

    /// <summary>
    ///     Maps the result to <c>201 Created</c> with a <c>Location</c> header and
    ///     <see cref="CommandResult{TResult}.Value" /> as the body on success, or a ProblemDetails response on failure.
    /// </summary>
    /// <typeparam name="TResult">The value produced on success.</typeparam>
    /// <param name="result">The command result to map.</param>
    /// <param name="location">The URI of the created resource, written to the <c>Location</c> header.</param>
    /// <param name="failureStatusCode">The HTTP status code used when the command failed. Defaults to <c>400</c>.</param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> or <paramref name="location" /> is <see langword="null" />.</exception>
    public static IResult ToCreatedHttpResult<TResult>(this CommandResult<TResult> result, string location,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(location);

        return result.IsSuccess
            ? TypedResults.Created(location, result.Value)
            : ToProblem(result, failureStatusCode);
    }

    /// <summary>
    ///     Maps the result to <c>201 Created</c> on success, computing the <c>Location</c> header from the produced
    ///     value (typically the new resource's id), or a ProblemDetails response on failure.
    /// </summary>
    /// <typeparam name="TResult">The value produced on success.</typeparam>
    /// <param name="result">The command result to map.</param>
    /// <param name="locationFactory">
    ///     Builds the URI of the created resource from the produced value. Only invoked when the command succeeded.
    /// </param>
    /// <param name="failureStatusCode">The HTTP status code used when the command failed. Defaults to <c>400</c>.</param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> or <paramref name="locationFactory" /> is <see langword="null" />.</exception>
    public static IResult ToCreatedHttpResult<TResult>(this CommandResult<TResult> result,
        Func<TResult, string> locationFactory, int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(locationFactory);

        return result.IsSuccess
            ? TypedResults.Created(locationFactory(result.Value!), result.Value)
            : ToProblem(result, failureStatusCode);
    }

    private static IResult ToProblem(CommandResult result, int statusCode)
    {
        // Title and type are left unset so ASP.NET Core fills in the RFC defaults for the status code. The error
        // code is a boxed int, one of the primitive extension types the framework's source-generated ProblemDetails
        // JSON context can serialize without reflection (keeps the response working under Native AOT).
        Dictionary<string, object?>? extensions = result.ErrorCode is { } errorCode
            ? new Dictionary<string, object?>(StringComparer.Ordinal) { [ErrorCodeExtensionName] = errorCode }
            : null;

        return TypedResults.Problem(result.ErrorMessage, statusCode: statusCode, extensions: extensions);
    }
}

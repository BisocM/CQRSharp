using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Maps a <see cref="CommandResult" /> / <see cref="CommandResult{TResult}" /> to a minimal-API
///     <see cref="IResult" />.
/// </summary>
/// <remarks>
///     <para>
///         A successful result becomes <c>204 No Content</c> (no value), <c>200 OK</c> (with a value) or
///         <c>201 Created</c> (the <c>ToCreatedHttpResult</c> overloads). A failed result becomes an RFC 7807
///         <c>application/problem+json</c> response whose status follows the result's
///         <see cref="CommandResult.ErrorKind" /> (see <see cref="GetStatusCode" />), whose <c>detail</c> is the
///         result's <see cref="CommandResult.ErrorMessage" />, and which carries the kind as the <c>errorKind</c>
///         extension member and <see cref="CommandResult.ErrorCode" />, when set, as <c>errorCode</c>. A
///         <see cref="CommandErrorKind.Validation" /> failure is written as a validation problem: its
///         <see cref="CommandResult.ValidationFailures" /> are grouped by member under <c>errors</c>, with their codes
///         under <c>errorCodes</c>, exactly like a <see cref="RequestValidationException" /> mapped by
///         <c>AddCqrsProblemDetails()</c>.
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

    /// <summary>The ProblemDetails extension member that carries <see cref="CommandResult.ErrorKind" /> (its name, e.g. <c>NotFound</c>).</summary>
    public const string ErrorKindExtensionName = "errorKind";

    /// <summary>
    ///     The default HTTP status for a failure of the given kind: <c>400</c> for <see cref="CommandErrorKind.Failure" />
    ///     and <see cref="CommandErrorKind.Validation" />, <c>401</c> for <see cref="CommandErrorKind.Unauthorized" />,
    ///     <c>403</c> for <see cref="CommandErrorKind.Forbidden" />, <c>404</c> for <see cref="CommandErrorKind.NotFound" />,
    ///     <c>409</c> for <see cref="CommandErrorKind.Conflict" />, <c>503</c> for <see cref="CommandErrorKind.Unavailable" />.
    /// </summary>
    /// <param name="errorKind">The kind of failure.</param>
    /// <returns>The status code. <see cref="CommandErrorKind.None" /> (a success) maps to <c>200</c>.</returns>
    public static int GetStatusCode(this CommandErrorKind errorKind)
        => errorKind switch
        {
            CommandErrorKind.None => StatusCodes.Status200OK,
            CommandErrorKind.Validation => StatusCodes.Status400BadRequest,
            CommandErrorKind.NotFound => StatusCodes.Status404NotFound,
            CommandErrorKind.Conflict => StatusCodes.Status409Conflict,
            CommandErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            CommandErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            CommandErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };

    /// <summary>
    ///     Maps the result to <c>204 No Content</c> on success, or a ProblemDetails response on failure.
    /// </summary>
    /// <param name="result">The command result to map.</param>
    /// <param name="failureStatusCode">
    ///     The HTTP status code used when the command failed, whatever its kind. Defaults to <see langword="null" />:
    ///     the status follows <see cref="CommandResult.ErrorKind" /> (see <see cref="GetStatusCode" />).
    /// </param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public static IResult ToHttpResult(this CommandResult result, int? failureStatusCode = null)
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
    /// <param name="failureStatusCode">
    ///     The HTTP status code used when the command failed, whatever its kind. Defaults to <see langword="null" />:
    ///     the status follows <see cref="CommandResult.ErrorKind" /> (see <see cref="GetStatusCode" />).
    /// </param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> is <see langword="null" />.</exception>
    public static IResult ToHttpResult<TResult>(this CommandResult<TResult> result, int? failureStatusCode = null)
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
    /// <param name="failureStatusCode">
    ///     The HTTP status code used when the command failed, whatever its kind. Defaults to <see langword="null" />:
    ///     the status follows <see cref="CommandResult.ErrorKind" /> (see <see cref="GetStatusCode" />).
    /// </param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> or <paramref name="location" /> is <see langword="null" />.</exception>
    public static IResult ToCreatedHttpResult(this CommandResult result, string location, int? failureStatusCode = null)
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
    /// <param name="failureStatusCode">
    ///     The HTTP status code used when the command failed, whatever its kind. Defaults to <see langword="null" />:
    ///     the status follows <see cref="CommandResult.ErrorKind" /> (see <see cref="GetStatusCode" />).
    /// </param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> or <paramref name="location" /> is <see langword="null" />.</exception>
    public static IResult ToCreatedHttpResult<TResult>(this CommandResult<TResult> result, string location, int? failureStatusCode = null)
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
    /// <param name="failureStatusCode">
    ///     The HTTP status code used when the command failed, whatever its kind. Defaults to <see langword="null" />:
    ///     the status follows <see cref="CommandResult.ErrorKind" /> (see <see cref="GetStatusCode" />).
    /// </param>
    /// <returns>The HTTP result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result" /> or <paramref name="locationFactory" /> is <see langword="null" />.</exception>
    public static IResult ToCreatedHttpResult<TResult>(this CommandResult<TResult> result,
        Func<TResult, string> locationFactory, int? failureStatusCode = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(locationFactory);

        return result.IsSuccess
            ? TypedResults.Created(locationFactory(result.Value!), result.Value)
            : ToProblem(result, failureStatusCode);
    }

    private static IResult ToProblem(CommandResult result, int? failureStatusCode)
    {
        // A validation failure is the same response a RequestValidationException produces - the same shape and, when
        // the app configured one, the same status - so a client sees one thing whether the pipeline or the handler
        // rejected the input.
        if (result.ErrorKind == CommandErrorKind.Validation)
            return new ValidationProblemResult(result, failureStatusCode);

        var statusCode = failureStatusCode ?? result.ErrorKind.GetStatusCode();

        // Title and type are left unset so ASP.NET Core fills in the RFC defaults for the status code. The extension
        // values are a string and a boxed int, two of the primitive extension types the framework's source-generated
        // ProblemDetails JSON context can serialize without reflection (keeps the response working under Native AOT).
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ErrorKindExtensionName] = result.ErrorKind.ToString()
        };
        if (result.ErrorCode is { } errorCode)
            extensions[ErrorCodeExtensionName] = errorCode;

        return TypedResults.Problem(result.ErrorMessage, statusCode: statusCode, extensions: extensions);
    }

    // The status of a validation result is decided when the response is written, where the request's services are at
    // hand: an explicit status wins, then CqrsProblemDetailsOptions.ValidationStatusCode (what the exception path uses),
    // then the kind's default. Until then StatusCode and Value.Status hold only the explicit status (null without one),
    // so a test can inspect the problem without running the response.
    private sealed class ValidationProblemResult : IResult, IStatusCodeHttpResult, IContentTypeHttpResult, IValueHttpResult,
        IValueHttpResult<ProblemDetails>, IValueHttpResult<HttpValidationProblemDetails>
    {
        private readonly int? _failureStatusCode;
        private readonly CommandErrorKind _errorKind;

        public ValidationProblemResult(CommandResult result, int? failureStatusCode)
        {
            _failureStatusCode = failureStatusCode;
            _errorKind = result.ErrorKind;
            Value = ValidationProblemDetailsFactory.Create(result.ValidationFailures, failureStatusCode ?? result.ErrorKind.GetStatusCode(), result.ErrorMessage);
            Value.Status = failureStatusCode;
            if (result.ErrorCode is { } errorCode)
                Value.Extensions[ErrorCodeExtensionName] = errorCode;
        }

        public HttpValidationProblemDetails Value { get; }

        object? IValueHttpResult.Value => Value;

        ProblemDetails? IValueHttpResult<ProblemDetails>.Value => Value;

        public int? StatusCode => _failureStatusCode;

        public string ContentType => "application/problem+json";

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            Value.Status = _failureStatusCode
                           ?? httpContext.RequestServices.GetService<IOptions<CqrsProblemDetailsOptions>>()?.Value.ValidationStatusCode
                           ?? _errorKind.GetStatusCode();

            return TypedResults.Problem(Value).ExecuteAsync(httpContext);
        }
    }
}

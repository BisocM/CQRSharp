# ASP.NET Core

```bash
dotnet add package CQRSharp.AspNetCore
```

`CQRSharp.AspNetCore` is the thin HTTP edge for a CQRSharp application. It is built for minimal APIs, has **no MVC
dependency**, uses no runtime reflection and is **Native-AOT compatible**. Its types live in the `CQRSharp.AspNetCore`
namespace; the `AddCqrsProblemDetails` registration extension lives in `Microsoft.Extensions.DependencyInjection`.

- [Result mapping](#result-mapping): `CommandResult` / `CommandResult<T>` to an `IResult`.
- [Exception mapping](#exception-mapping): the pipeline's known exceptions to RFC 7807 ProblemDetails.
- [Idempotency-Key](#idempotency-key): read and validate the request header.
- [A complete endpoint](#a-complete-endpoint)
- [Native AOT](#native-aot)
- [Why there is no MapCommand](#why-there-is-no-mapcommand)

`samples/CQRSharp.Sample.AspNetCore` is a runnable minimal API over the package; CI runs its self-test, JIT-compiled and
as a Native AOT binary.

## Result mapping

Handlers report expected failures through `CommandResult` rather than exceptions. The `ToHttpResult` family turns that
result into a minimal-API `IResult`:

| Call | On success | On failure |
|------|------------|------------|
| `CommandResult.ToHttpResult()` | `204 No Content` | ProblemDetails |
| `CommandResult<T>.ToHttpResult()` | `200 OK` with `Value` as the body | ProblemDetails |
| `CommandResult.ToCreatedHttpResult(location)` | `201 Created` + `Location` | ProblemDetails |
| `CommandResult<T>.ToCreatedHttpResult(location)` | `201 Created` + `Location`, `Value` as the body | ProblemDetails |
| `CommandResult<T>.ToCreatedHttpResult(value => location)` | same, `Location` computed from `Value` | ProblemDetails (the factory is not invoked) |

The status of a failure follows the result's `ErrorKind`:

| `ErrorKind` | Status |
|-------------|--------|
| `Failure` | **400** |
| `Validation` | **400** (`CqrsProblemDetailsOptions.ValidationStatusCode`), as a validation problem (below) |
| `Unauthorized` | **401** |
| `Forbidden` | **403** |
| `NotFound` | **404** |
| `Conflict` | **409** |
| `Unavailable` | **503** |

`CommandErrorKind.GetStatusCode()` exposes the same table. Every overload also takes an optional `failureStatusCode`;
when given, it is used for every failure regardless of kind.

```csharp
app.MapDelete("/orders/{id:guid}", async (Guid id, ICqrsDispatcher dispatcher, CancellationToken ct) =>
{
    var result = await dispatcher.Send(new CancelOrder { OrderId = id }, ct);
    return result.ToHttpResult();   // NotFound -> 404, Conflict -> 409, ...
});
```

A failure is written as `application/problem+json`: `ErrorMessage` becomes `detail`, the kind becomes the `errorKind`
extension member, and `ErrorCode` (when set) becomes `errorCode`. `title` and `type` are left to ASP.NET Core's defaults
for the status code:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "Order not found.",
  "errorKind": "NotFound",
  "errorCode": 1004
}
```

A `Validation` failure is written as the same `HttpValidationProblemDetails` the exception mapping below produces
(`errors` grouped by member, `errorCodes` alongside), so a client sees one shape whether the pipeline or the handler
rejected the input. Its status is the configured `ValidationStatusCode` unless the call passes `failureStatusCode`. The
returned `IResult` implements `IStatusCodeHttpResult`, `IContentTypeHttpResult` and
`IValueHttpResult<HttpValidationProblemDetails>`, so an endpoint unit test can inspect it; its `StatusCode` is `null`
unless an explicit status was passed, because the configured status is only read when the result executes.

`ErrorCode` is an application-defined number, so the package never interprets it as an HTTP status. If the host
registered `AddProblemDetails(...)`, its `CustomizeProblemDetails` callback applies to these responses too.

> Overload resolution is static. A `CommandResult<T>` held in a variable typed as the base `CommandResult` maps to `204`
> and its value is not written.

## Exception mapping

The pipeline behaviors signal some conditions by throwing. `AddCqrsProblemDetails()` registers an ASP.NET Core
`IExceptionHandler` that turns the known, user-safe ones into ProblemDetails responses:

```csharp
builder.Services.AddCqrsProblemDetails();

var app = builder.Build();
app.UseExceptionHandler(); // the handler only runs when the middleware is in the pipeline
```

| Exception | Thrown by | Status | `detail` |
|-----------|-----------|--------|----------|
| `RequestValidationException` | the validation behavior | **400** | `"Validation failed."`, plus `errors` / `errorCodes` |
| `DuplicateRequestException` | `UseIdempotency(...)` | **409** | the exception message (names the idempotency key the client sent) |
| `IdempotencyKeyMismatchException` | `UseIdempotency(...)` | **422** | the exception message: the key was reused with a different payload |
| `RateLimitExceededException` | `UseRateLimiting(...)` | **429** | fixed text; the message names the server's request type |
| `RequestTimeoutException` | `UseTimeout(...)` | **504** | fixed text; the message names the server's request type |
| `BackgroundTaskRejectedException` | a `RunMode.Queued` dispatch the background queue did not run | **503** | fixed text; the message describes the server's queue |
| `InvalidIdempotencyKeyException` | `GetIdempotencyKey()` | **400** | the exception message (never echoes the header value) |

Every other exception is **left unhandled** (`TryHandleAsync` returns `false`), so it flows on to your other
`IExceptionHandler`s and the host's default 500 handling. That includes a `TimeoutException` from a dependency (a
database driver, an HTTP client): only the timeout behavior's own `RequestTimeoutException` is mapped. No stack trace,
exception type or inner exception is ever written. Once the response has started, the handler leaves the exception to
the host.

`Retry-After` is sent with two responses, in whole seconds, rounded up, at least 1:

- a `429`, from `RateLimitExceededException.RetryAfter`: the time until the caller's bucket holds a token again. No
  header when the exception does not carry one.
- a `409` for a duplicate whose original is still running (`DuplicateRequestException.IsInProgress`), from
  `DuplicateInProgressRetryAfter` (1 second by default).

A validation failure is a standard `HttpValidationProblemDetails`: messages are grouped by `ValidationFailure.MemberName`
under `errors`, and failures that name no member are grouped under the empty key (the ASP.NET Core convention). The
stable `ValidationFailure.Code` values are carried in a parallel `errorCodes` extension, in the same order:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "Validation failed.",
  "errors": {
    "Quantity": ["Quantity must be positive."],
    "": ["The request is inconsistent."]
  },
  "errorCodes": {
    "Quantity": ["QUANTITY_RANGE"],
    "": ["REQUEST_INVALID"]
  }
}
```

> If an `IRequestExceptionHandler` inside the pipeline handles one of these exceptions (see
> [Pipeline behaviors](pipeline-behaviors.md#exception-handling)), it never reaches ASP.NET Core and this mapping does
> not run.

### Logging

The handler logs every exception it maps as one line without the stack trace, at the level CQRSharp's logging behavior
gives the same exception: event **7001** at Warning for `RequestTimeoutException` and `BackgroundTaskRejectedException`
(the server could not serve the request), and event **7000** at Information for the others (the caller has to act). The
level follows the exception type, not the status code. See [Observability](observability.md#logging).

On .NET 8 and .NET 9, ASP.NET Core's `ExceptionHandlerMiddleware` also logs every exception at Error, with its stack
trace, before the handler maps it (category `Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware`). CQRSharp
cannot suppress that line. .NET 10 does not log an exception an `IExceptionHandler` handled. To silence it on .NET 8
and 9, filter that category, knowing that the filter also hides the line for exceptions nobody handles:

```csharp
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
```

### ProblemDetails customization

`AddCqrsProblemDetails()` also calls `AddProblemDetails()`, and the handler writes through `IProblemDetailsService`. Your
`CustomizeProblemDetails` callback and any custom `IProblemDetailsWriter` therefore apply, and the callback receives the
original exception in `context.Exception`:

```csharp
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddCqrsProblemDetails();
```

When no writer accepts the request (for example an `Accept` header that excludes JSON), the handler still writes the same
ProblemDetails payload directly rather than returning an empty error. Calling `AddCqrsProblemDetails` more than once
registers the handler once.

### Options

Each mapping has a nullable status code on `CqrsProblemDetailsOptions`: set it to override the status, or to `null` to
disable that mapping (the exception is then left unhandled).

```csharp
builder.Services.AddCqrsProblemDetails(o =>
{
    o.ValidationStatusCode = StatusCodes.Status422UnprocessableEntity;
    o.TimeoutStatusCode = null;                        // let timeouts reach the host's default handling
});
```

| Option | Default |
|--------|---------|
| `ValidationStatusCode` | `400` |
| `DuplicateRequestStatusCode` | `409` |
| `IdempotencyKeyMismatchStatusCode` | `422` |
| `RateLimitStatusCode` | `429` |
| `TimeoutStatusCode` | `504` |
| `BackgroundTaskRejectedStatusCode` | `503` |
| `InvalidIdempotencyKeyStatusCode` | `400` |
| `DuplicateInProgressRetryAfter` | 1 second; `null` sends no `Retry-After` with the in-progress `409` |

`ValidationStatusCode` also decides the status of a `CommandResult.Invalid(...)` written with `ToHttpResult()` (unless
the call passes an explicit `failureStatusCode`), so the handler-side and the pipeline-side validation problem carry one
status as well as one shape; both carry `errorKind: "Validation"`.

## Idempotency-Key

`IIdempotentRequest` needs a stable key, and over HTTP that key conventionally arrives in the `Idempotency-Key` request
header. Two `HttpContext` extensions read and validate it; **nothing is assigned to a request implicitly**: you copy the
key onto your command.

```csharp
// Required: throws InvalidIdempotencyKeyException (mapped to a 400 ProblemDetails) when missing or malformed.
string key = http.GetIdempotencyKey();

// Optional / custom handling: false when the header is missing or malformed.
if (http.TryGetIdempotencyKey(out var optionalKey, maxLength: 64)) { /* ... */ }
```

A key is accepted when the header is sent **exactly once** and, after trimming whitespace and one pair of surrounding
double quotes (the IETF draft sends a quoted string; most clients send it bare), it is:

- non-empty,
- at most `maxLength` characters, by default `IdempotencyKeyHttpExtensions.DefaultMaxLength` (**255**),
- made only of visible ASCII characters other than `"`, `\` and `,` (a comma is how HTTP folds repeated headers into
  one value, so it would hide a second key).

The key is returned exactly as sent: the idempotency stores compare keys ordinally and case-sensitively, so no case
folding is applied. The header *name* (`IdempotencyKeyHttpExtensions.HeaderName`) is matched case-insensitively, as
HTTP requires.

**Scope the key by the caller.** The idempotency stores' key space is global, and a key a client chose cannot be
trusted to be unique across clients: two users who send the same key would have the second answered with the first
one's result. Prefix the header value with the authenticated caller's (or tenant's) identity, as the endpoint below
does, and keep the prefix short enough that the whole key stays within 450 characters, the most the EF Core store
holds (see [Key guidance](idempotency-and-resilience.md#key-guidance)).

`InvalidIdempotencyKeyException` derives from `BadHttpRequestException` and carries `StatusCode` 400, so error handling
you already have for bad requests recognizes it without knowing about CQRSharp.

## A complete endpoint

```csharp
using System.Security.Claims;
using CQRSharp;
using CQRSharp.AspNetCore;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddCqrsGenerated(b => b.UseIdempotency());
builder.Services.AddCqrsProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

app.MapPost("/orders", async (PlaceOrderBody body, HttpContext http, ICqrsDispatcher dispatcher, CancellationToken ct) =>
{
    var caller = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? throw new InvalidOperationException("The endpoint requires an authenticated caller.");

    var command = new PlaceOrder
    {
        OrderId = body.OrderId,
        Sku = body.Sku,
        Quantity = body.Quantity,
        IdempotencyKey = $"{caller}:{http.GetIdempotencyKey()}"
    };

    var result = await dispatcher.Send(command, ct);
    return result.ToCreatedHttpResult($"/orders/{command.OrderId}");
}).RequireAuthorization();

app.Run();

public sealed record PlaceOrderBody(Guid OrderId, string Sku, int Quantity);

public sealed class PlaceOrder : CommandBase, IIdempotentRequest
{
    public required Guid OrderId { get; init; }
    public required string Sku { get; init; }
    public required int Quantity { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrder>
{
    public Task<CommandResult> Handle(PlaceOrder command, CancellationToken cancellationToken)
        => Task.FromResult(command.Quantity > 0
            ? CommandResult.FromSuccess()
            : CommandResult.Invalid(new ValidationFailure("QUANTITY_RANGE", "Quantity must be positive.", nameof(command.Quantity))));
}
```

The client chooses the order id, and a retry sends the same one: the automatic payload fingerprint covers `OrderId`, so
a retry with a new id under the same key would be a `422`. Because `PlaceOrder` returns a plain `CommandResult`, a
completed request's duplicate is replayed with no serializer.

| Request | Response |
|---------|----------|
| valid, first time | `201 Created`, `Location: /orders/{orderId}` |
| same `Idempotency-Key` again, original finished | the original `201 Created`, replayed; the handler does not run again |
| same `Idempotency-Key` again, original still running | `409` ProblemDetails + `Retry-After: 1` (`DuplicateInProgressRetryAfter`) |
| same `Idempotency-Key` with a different body | `422` ProblemDetails (from `IdempotencyKeyMismatchException`) |
| no `Idempotency-Key` header | `400` ProblemDetails, `"The Idempotency-Key header is required."` |
| `quantity: 0` | `400` validation ProblemDetails with `"errors": { "Quantity": [...] }` and `"errorKind": "Validation"` |

The body is bound to a small DTO rather than to the command itself. Binding a request type from the body is possible
(its `Context` cannot be set from outside: the dispatcher always builds it from the context factory), but the DTO keeps
the HTTP contract separate from the command and lets the endpoint add what the client does not send, such as the
caller's identity in the key.

## Native AOT

The package is annotated `IsAotCompatible` and builds clean under the AOT analyzers. Two details keep the responses
working without reflection-based JSON:

- ProblemDetails extension members are serialized as `object`. The package only ever adds a `string` (`errorKind`), an
  `int` (`errorCode`) and a pre-rendered `JsonElement` (`errorCodes`), all of which ASP.NET Core's source-generated
  ProblemDetails JSON context can write.
- Request bodies and success bodies (`CommandResult<T>.Value`) are serialized with the host's HTTP JSON options. As with
  any AOT minimal API, register your own types in a `JsonSerializerContext`:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

[JsonSerializable(typeof(PlaceOrderBody))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

## Why there is no MapCommand

A library-side `MapCommand<TCommand>(pattern)` would have to call `MapPost(pattern, Delegate)` with an open generic
delegate. The Request Delegate Generator only intercepts `Map*` calls it can analyze statically in *your* project, so a
call inside a library falls back to the reflection-based `RequestDelegateFactory`, which is annotated
`RequiresUnreferencedCode` / `RequiresDynamicCode`: it cannot be made AOT-analyzer-clean without suppressing warnings.
Writing the endpoint yourself, as above, keeps binding explicit and lets the generator produce the AOT-safe delegate.

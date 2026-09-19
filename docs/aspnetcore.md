# ASP.NET Core

```bash
dotnet add package CQRSharp.AspNetCore
```

`CQRSharp.AspNetCore` is the thin HTTP edge for a CQRSharp application. It is built for minimal APIs, has
**no MVC dependency**, uses no runtime reflection and is **Native-AOT-compatible**. Everything lives in
the `CQRSharp.AspNetCore` namespace:

- [Result mapping](#result-mapping) — `CommandResult` / `CommandResult<T>` to an `IResult`.
- [Exception mapping](#exception-mapping) — the framework's known exceptions to RFC 7807 ProblemDetails.
- [Idempotency-Key](#idempotency-key) — read and validate the request header.
- [A complete endpoint](#a-complete-endpoint)
- [Native AOT](#native-aot)

It deliberately does **not** ship `MapCommand<T>()` / `MapQuery<T>()` endpoint sugar; see
[Why there is no MapCommand](#why-there-is-no-mapcommand).

## Result mapping

Handlers report expected failures through `CommandResult` rather than exceptions. The `ToHttpResult`
family turns that result into a minimal-API `IResult`:

| Call | On success | On failure |
|------|------------|------------|
| `CommandResult.ToHttpResult()` | `204 No Content` | ProblemDetails |
| `CommandResult<T>.ToHttpResult()` | `200 OK` with `Value` as the body | ProblemDetails |
| `CommandResult.ToCreatedHttpResult(location)` | `201 Created` + `Location` | ProblemDetails |
| `CommandResult<T>.ToCreatedHttpResult(location)` | `201 Created` + `Location`, `Value` as the body | ProblemDetails |
| `CommandResult<T>.ToCreatedHttpResult(value => location)` | same, `Location` computed from `Value` | ProblemDetails (the factory is not invoked) |

Every overload takes an optional `failureStatusCode` (default **400**).

```csharp
app.MapDelete("/orders/{id:guid}", async (Guid id, ICqrsDispatcher dispatcher, CancellationToken ct) =>
{
    var result = await dispatcher.Send(new CancelOrder { OrderId = id }, ct);
    return result.ToHttpResult(result.ErrorCode == 404 ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
});
```

A failure is written as `application/problem+json`. `CommandResult` carries exactly two pieces of error
information and both are surfaced: `ErrorMessage` becomes `detail`, and `ErrorCode` (when set) becomes the
`errorCode` extension member. `title` and `type` are left to ASP.NET Core's defaults for the status code:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "Order not found.",
  "errorCode": 404
}
```

`ErrorCode` is an application-defined number, so the package never interprets it as an HTTP status — map
it yourself as above if that is your convention. If the host registered `AddProblemDetails(...)`, its
`CustomizeProblemDetails` callback applies to these responses too.

> Overload resolution is static. A `CommandResult<T>` held in a variable typed as the base `CommandResult`
> maps to `204` and its value is not written.

## Exception mapping

The pipeline behaviors signal some conditions by throwing. `AddCqrsProblemDetails()` registers an ASP.NET
Core `IExceptionHandler` that turns the known, user-safe ones into ProblemDetails responses:

```csharp
builder.Services.AddCqrsProblemDetails();

var app = builder.Build();
app.UseExceptionHandler(); // the handler only runs when the middleware is in the pipeline
```

| Exception | Thrown by | Status | `detail` |
|-----------|-----------|--------|----------|
| `RequestValidationException` | `UseValidation()` | **400** | the exception message, plus `errors` / `errorCodes` |
| `DuplicateRequestException` | `UseIdempotency(...)` | **409** | the exception message (names the idempotency key the client sent) |
| `RateLimitExceededException` | `UseRateLimiting(...)` | **429** | fixed text — the exception message embeds the internal request id and user id |
| `TimeoutException` | `UseTimeout(...)` | **504** | fixed text — a `TimeoutException` can originate in any dependency, so its message is not assumed safe |
| `InvalidIdempotencyKeyException` | `GetIdempotencyKey()` | **400** | the exception message (never echoes the header value) |

Every other exception is **left unhandled** (`TryHandleAsync` returns `false`), so it flows on to your
other `IExceptionHandler`s and the host's default 500 handling. No stack trace, exception type or inner
exception is ever written.

A validation failure is a standard `HttpValidationProblemDetails`: messages are grouped by
`ValidationFailure.MemberName` under `errors`, and failures that name no member are grouped under the
empty key (the ASP.NET Core convention). The stable `ValidationFailure.Code` values are carried in a
parallel `errorCodes` extension, in the same order:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "Validation failed for request 'PlaceOrder'.",
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

### ProblemDetails customization

`AddCqrsProblemDetails()` also calls `AddProblemDetails()` (idempotent), and the handler writes through
`IProblemDetailsService`. Your `CustomizeProblemDetails` callback and any custom `IProblemDetailsWriter`
therefore apply, and the callback receives the original exception in `context.Exception`:

```csharp
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddCqrsProblemDetails();
```

When no writer accepts the request (for example an `Accept` header that excludes JSON), the handler still
writes the same ProblemDetails payload directly rather than returning an empty error.

### Options

Each mapping has a nullable status code on `CqrsProblemDetailsOptions`: set it to override the status, or
to `null` to disable that mapping (the exception is then left unhandled).

```csharp
builder.Services.AddCqrsProblemDetails(o =>
{
    o.ValidationStatusCode = StatusCodes.Status422UnprocessableEntity;
    o.TimeoutStatusCode = null;                        // let timeouts reach the host's default handling
    o.RateLimitRetryAfter = TimeSpan.FromSeconds(30);  // emits "Retry-After: 30" on 429 responses
});
```

| Option | Default |
|--------|---------|
| `ValidationStatusCode` | `400` |
| `DuplicateRequestStatusCode` | `409` |
| `RateLimitStatusCode` | `429` |
| `TimeoutStatusCode` | `504` |
| `InvalidIdempotencyKeyStatusCode` | `400` |
| `RateLimitRetryAfter` | `null` (no `Retry-After` header) |
| `DuplicateInProgressRetryAfter` | 1 second — sent with the `409` for a duplicate whose original is still running (`DuplicateRequestException.IsInProgress`); `null` omits the header |

`RateLimitExceededException` does not carry the limiter's window, so `Retry-After` cannot be derived from
the exception. Set `RateLimitRetryAfter` to the window you configured in `UseRateLimiting(...)`; it is
rounded up to whole seconds.

> If you enable `UseExceptionHandling()` and an `IRequestExceptionHandler` swallows one of these
> exceptions inside the pipeline, it never reaches ASP.NET Core and this mapping does not run.

## Idempotency-Key

`IIdempotentRequest` needs a stable key, and over HTTP that key conventionally arrives in the
`Idempotency-Key` request header. Two `HttpContext` extensions read and validate it; **nothing is assigned
to a request implicitly** — you copy the key onto your command.

```csharp
// Required: throws InvalidIdempotencyKeyException (mapped to a 400 ProblemDetails) when missing or malformed.
string key = http.GetIdempotencyKey();

// Optional / custom handling: false when the header is missing or malformed.
if (http.TryGetIdempotencyKey(out var optionalKey, maxLength: 64)) { /* ... */ }
```

A key is accepted when the header is sent **exactly once** and, after trimming whitespace and one pair of
surrounding double quotes (the IETF draft sends a quoted string; most clients send it bare), it is:

- non-empty,
- at most `maxLength` characters — default `IdempotencyKeyHttpExtensions.DefaultMaxLength` (**255**),
  which sits inside the 512-character bound `IIdempotentRequest.IdempotencyKey` documents, so an accepted
  key fits every store backend,
- made only of visible ASCII characters other than `"`, `\` and `,` (a comma is how HTTP folds repeated
  headers into one value, so it would hide a second key).

The key is returned exactly as sent — the idempotency stores compare keys with ordinal, case-sensitive
equality, so no case folding is applied. The header *name* is matched case-insensitively, as HTTP requires.

`InvalidIdempotencyKeyException` derives from `BadHttpRequestException` and carries `StatusCode` 400, so
error handling you already have for bad requests recognizes it without knowing about CQRSharp.

## A complete endpoint

```csharp
using CQRSharp;
using CQRSharp.AspNetCore;
using CQRSharp.Pipelines;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddCqrsGenerated(b => b
    .UseValidation()
    .UseIdempotency());
builder.Services.AddCqrsProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

app.MapPost("/orders", async (PlaceOrderBody body, HttpContext http, ICqrsDispatcher dispatcher, CancellationToken ct) =>
{
    var command = new PlaceOrder
    {
        Sku = body.Sku,
        Quantity = body.Quantity,
        IdempotencyKey = http.GetIdempotencyKey()
    };

    var result = await dispatcher.Send(command, ct);
    return result.ToCreatedHttpResult(orderId => $"/orders/{orderId}", StatusCodes.Status409Conflict);
});

app.Run();

public sealed record PlaceOrderBody(string Sku, int Quantity);

public sealed class PlaceOrder : ResultCommandBase<Guid>, IIdempotentRequest
{
    public required string Sku { get; init; }
    public required int Quantity { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed class PlaceOrderHandler : IResultCommandHandler<PlaceOrder, Guid>
{
    public Task<CommandResult<Guid>> Handle(PlaceOrder command, CancellationToken cancellationToken)
        => Task.FromResult(command.Quantity > 0
            ? CommandResult<Guid>.FromSuccess(Guid.NewGuid())
            : CommandResult<Guid>.FromError("Quantity must be positive.", errorCode: 1001));
}
```

| Request | Response |
|---------|----------|
| valid, first time | `201 Created`, `Location: /orders/{id}`, the id as the body |
| same `Idempotency-Key` again, original finished | `409` ProblemDetails (from `DuplicateRequestException`) — or, with [`ReplayResultsWith(...)`](idempotency-and-resilience.md#what-can-be-replayed) configured for `CommandResult<Guid>`, the original `201 Created` again |
| same `Idempotency-Key` again, original still running | `409` ProblemDetails + `Retry-After: 1` (`DuplicateInProgressRetryAfter`) |
| no `Idempotency-Key` header | `400` ProblemDetails, `"The Idempotency-Key header is required."` |
| `quantity: 0` | `409` ProblemDetails with `"errorCode": 1001` (the `failureStatusCode` passed above) |

The body is bound to a small DTO rather than to the command itself: a CQRSharp request also carries
`Context` and `Metadata`, which should not be settable from a request body.

## Native AOT

The package is annotated `IsAotCompatible` and builds clean under the AOT analyzers. Two details keep the
responses working without reflection-based JSON:

- ProblemDetails extension members are serialized as `object`. The package only ever adds an `int`
  (`errorCode`) and a pre-rendered `JsonElement` (`errorCodes`), both of which ASP.NET Core's
  source-generated ProblemDetails JSON context can write.
- Success bodies (`CommandResult<T>.Value`) are serialized with the host's HTTP JSON options. As with any
  AOT minimal API, register your own types in a `JsonSerializerContext`:

```csharp
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

[JsonSerializable(typeof(PlaceOrderBody))]
[JsonSerializable(typeof(Guid))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

## Why there is no MapCommand

A library-side `MapCommand<TCommand>(pattern)` would have to call `MapPost(pattern, Delegate)` with an open
generic delegate. The Request Delegate Generator only intercepts `Map*` calls it can analyze statically in
*your* project, so a call inside a library falls back to the reflection-based `RequestDelegateFactory`,
which is annotated `RequiresUnreferencedCode` / `RequiresDynamicCode` — it cannot be made AOT-analyzer-clean
without suppressing warnings. Binding the command type straight from the body would also expose `Context`
and `Metadata` to the client. Writing the three-line endpoint yourself (above) keeps binding explicit and
lets the generator produce the AOT-safe delegate.

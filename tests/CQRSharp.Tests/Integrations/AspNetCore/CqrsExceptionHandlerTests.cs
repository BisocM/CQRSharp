using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization.Metadata;
using CQRSharp.AspNetCore;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Integrations.AspNetCore;

/// <summary>The AddCqrsProblemDetails exception handler, observed through the real exception-handler middleware.</summary>
public sealed class CqrsExceptionHandlerTests
{
    private sealed class AspNetCoreTestRequestMarker;

    private static Task<AspNetCoreTestHost> StartAsync(Func<Exception> exceptionFactory,
        Action<IServiceCollection>? configureServices = null)
    {
        return AspNetCoreTestHost.StartAsync(
            configureServices ?? (services => services.AddCqrsProblemDetails()),
            app =>
            {
                app.UseExceptionHandler();
                app.MapGet("/", IResult () => throw exceptionFactory());
            });
    }

    private static RequestValidationException ValidationException()
    {
        return new RequestValidationException(typeof(AspNetCoreTestRequestMarker),
        [
            new ValidationFailure("NAME_REQUIRED", "Name is required.", "Name"),
            new ValidationFailure("NAME_TOO_SHORT", "Name is too short.", "Name"),
            new ValidationFailure("AGE_RANGE", "Age must be positive.", "Age"),
            new ValidationFailure("REQUEST_INVALID", "The request is inconsistent.")
        ]);
    }

    private static RateLimitExceededException RateLimitException()
    {
        var context = Mock.Of<IRateLimitedContext>(c => c.RequestId == "req-internal-1" && c.UserId == "user-internal-9");
        return new RateLimitExceededException(context, "Rate limit exceeded for user.");
    }

    [Fact(DisplayName = "RequestValidationException maps to a 400 validation problem with errors and codes grouped by member")]
    public async Task Validation_maps_to_400_validation_problem()
    {
        await using var host = await StartAsync(ValidationException);

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("detail").GetString().Should().Be("Validation failed for request 'AspNetCoreTestRequestMarker'.");

        var errors = body.GetProperty("errors");
        errors.GetProperty("Name").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Name is required.", "Name is too short.");
        errors.GetProperty("Age").EnumerateArray().Select(e => e.GetString()).Should().Equal("Age must be positive.");
        errors.GetProperty("").EnumerateArray().Select(e => e.GetString()).Should().Equal("The request is inconsistent.");

        var codes = body.GetProperty("errorCodes");
        codes.GetProperty("Name").EnumerateArray().Select(e => e.GetString()).Should().Equal("NAME_REQUIRED", "NAME_TOO_SHORT");
        codes.GetProperty("Age").EnumerateArray().Select(e => e.GetString()).Should().Equal("AGE_RANGE");
        codes.GetProperty("").EnumerateArray().Select(e => e.GetString()).Should().Equal("REQUEST_INVALID");
    }

    [Fact(DisplayName = "DuplicateRequestException maps to 409 with the exception message as the detail")]
    public async Task Duplicate_maps_to_409()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17"));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(409);
        body.GetProperty("title").GetString().Should().Be("Conflict");
        body.GetProperty("detail").GetString().Should().Be("A request with idempotency key 'order-17' has already been processed.");
        response.Headers.RetryAfter.Should().BeNull("the original already completed; asking again will not change the answer");
    }

    [Fact(DisplayName = "A duplicate whose original is still running maps to 409 with Retry-After, so the client asks again and gets the replay")]
    public async Task In_progress_duplicate_carries_retry_after()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17", isInProgress: true));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("detail").GetString().Should().Be("A request with idempotency key 'order-17' is still being processed.");
    }

    [Fact(DisplayName = "RateLimitExceededException maps to 429 without leaking the request or user id, and without Retry-After by default")]
    public async Task Rate_limit_maps_to_429()
    {
        await using var host = await StartAsync(RateLimitException);

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be((HttpStatusCode)429);
        response.Headers.RetryAfter.Should().BeNull();
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("req-internal-1").And.NotContain("user-internal-9");
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(429);
        body.GetProperty("detail").GetString().Should().Be("Rate limit exceeded. Try again later.");
    }

    [Fact(DisplayName = "RateLimitExceededException sets Retry-After (whole seconds, rounded up) when configured")]
    public async Task Rate_limit_sets_retry_after_when_configured()
    {
        await using var host = await StartAsync(RateLimitException,
            services => services.AddCqrsProblemDetails(o => o.RateLimitRetryAfter = TimeSpan.FromSeconds(29.2)));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be((HttpStatusCode)429);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact(DisplayName = "TimeoutException maps to 504 with a fixed detail (its message may come from anywhere)")]
    public async Task Timeout_maps_to_504()
    {
        await using var host = await StartAsync(() => new TimeoutException("connection string Server=internal-db timed out"));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("internal-db");
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(504);
        body.GetProperty("detail").GetString().Should().Be("The request timed out.");
    }

    [Fact(DisplayName = "InvalidIdempotencyKeyException maps to 400 with the exception message as the detail")]
    public async Task Invalid_idempotency_key_maps_to_400()
    {
        await using var host = await AspNetCoreTestHost.StartAsync(
            services => services.AddCqrsProblemDetails(),
            app =>
            {
                app.UseExceptionHandler();
                app.MapPost("/", (HttpContext context) => Results.Text(context.GetIdempotencyKey()));
            });

        var response = await host.Client.PostAsync("/", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("detail").GetString().Should().Be("The Idempotency-Key header is required.");
    }

    [Fact(DisplayName = "An unknown exception is not handled: the host's default 500 applies and the message is not written")]
    public async Task Unknown_exception_is_not_handled()
    {
        await using var host = await StartAsync(() => new InvalidOperationException("secret internal state"));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("secret internal state");
    }

    [Fact(DisplayName = "TryHandleAsync returns false and leaves the response untouched for an unknown exception")]
    public async Task Try_handle_returns_false_for_unknown_exception()
    {
        var handler = new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()));
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        handled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact(DisplayName = "Status codes are overridable per mapping")]
    public async Task Status_codes_are_overridable()
    {
        await using var host = await StartAsync(ValidationException,
            services => services.AddCqrsProblemDetails(o => o.ValidationStatusCode = StatusCodes.Status422UnprocessableEntity));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(422);
        body.GetProperty("errors").GetProperty("Name").GetArrayLength().Should().Be(2);
    }

    [Fact(DisplayName = "A mapping set to null is disabled: the exception falls through to the host's default 500")]
    public async Task Null_status_disables_the_mapping()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17"),
            services => services.AddCqrsProblemDetails(o => o.DuplicateRequestStatusCode = null));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact(DisplayName = "The host's CustomizeProblemDetails applies, and sees the exception")]
    public async Task Host_problem_details_customization_applies()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17"), services =>
        {
            services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["service"] = "orders";
                context.ProblemDetails.Extensions["exceptionType"] = context.Exception?.GetType().Name;
            });
            services.AddCqrsProblemDetails();
        });

        var body = await AspNetCoreTestHost.ReadJsonAsync(await host.Client.GetAsync("/"));

        body.GetProperty("service").GetString().Should().Be("orders");
        body.GetProperty("exceptionType").GetString().Should().Be(nameof(DuplicateRequestException));
    }

    [Fact(DisplayName = "When no ProblemDetails writer accepts the request (Accept excludes JSON), the problem is still written")]
    public async Task Falls_back_when_problem_details_service_declines()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AspNetCoreTestHost.ReadJsonAsync(response)).GetProperty("status").GetInt32().Should().Be(409);
    }

    [Fact(DisplayName = "The handler writes the problem itself when no IProblemDetailsService is registered")]
    public async Task Writes_directly_without_problem_details_service()
    {
        var handler = new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()));
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = body }
        };

        var handled = await handler.TryHandleAsync(context, ValidationException(), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var document = System.Text.Json.JsonDocument.Parse(body.ToArray());
        document.RootElement.GetProperty("errors").GetProperty("Age").GetArrayLength().Should().Be(1);
        document.RootElement.GetProperty("errorCodes").GetProperty("Age")[0].GetString().Should().Be("AGE_RANGE");
    }

    [Fact(DisplayName = "The validation problem serializes with reflection-based JSON removed (the Native AOT configuration)")]
    public async Task Validation_problem_serializes_without_reflection_resolver()
    {
        await using var host = await StartAsync(ValidationException, services =>
        {
            services.AddCqrsProblemDetails();
            // Emulates a Native AOT app: only the framework's source-generated ProblemDetails context remains.
            services.PostConfigure<JsonOptions>(options =>
            {
                var chain = options.SerializerOptions.TypeInfoResolverChain;
                var reflectionResolvers = chain.OfType<DefaultJsonTypeInfoResolver>().ToList();
                reflectionResolvers.Should().NotBeEmpty("the test must actually strip the reflection fallback");
                foreach (var resolver in reflectionResolvers)
                    chain.Remove(resolver);
            });
        });

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("errors").GetProperty("Name").GetArrayLength().Should().Be(2);
        body.GetProperty("errorCodes").GetProperty("Name").GetArrayLength().Should().Be(2);
    }

    [Fact(DisplayName = "AddCqrsProblemDetails is idempotent: repeated calls register the handler once")]
    public void Registration_is_idempotent()
    {
        var services = new ServiceCollection();

        services.AddCqrsProblemDetails();
        services.AddCqrsProblemDetails(o => o.TimeoutStatusCode = 408);

        services.Count(d => d.ServiceType == typeof(IExceptionHandler)).Should().Be(1);
    }
}

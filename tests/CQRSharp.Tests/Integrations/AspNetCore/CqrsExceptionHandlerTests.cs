using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CQRSharp.AspNetCore;
using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    private static RateLimitExceededException RateLimitException(TimeSpan? retryAfter = null)
        => new($"The rate limit for {nameof(AspNetCoreTestRequestMarker)} was exceeded.", retryAfter);

    [Fact(DisplayName = "RequestValidationException maps to a 400 validation problem with errors and codes grouped by member")]
    public async Task Validation_maps_to_400_validation_problem()
    {
        await using var host = await StartAsync(ValidationException);

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("detail").GetString().Should().Be("Validation failed.");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().NotContain(nameof(AspNetCoreTestRequestMarker), "the server's request type is not the caller's business");

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

    [Fact(DisplayName = "A pipeline validation failure and a handler's CommandResult.Invalid produce the same response body")]
    public async Task Validation_exception_and_invalid_result_bodies_match()
    {
        await using var fromPipeline = await StartAsync(ValidationException);
        await using var fromHandler = await AspNetCoreTestHost.StartAsync(
            services => services.AddCqrsProblemDetails(),
            app => app.MapGet("/", () => CommandResult.Invalid(ValidationException().Failures).ToHttpResult()));

        var pipelineResponse = await fromPipeline.Client.GetAsync("/", TestContext.Current.CancellationToken);
        var handlerResponse = await fromHandler.Client.GetAsync("/", TestContext.Current.CancellationToken);

        handlerResponse.StatusCode.Should().Be(pipelineResponse.StatusCode);
        (await BodyWithoutTraceId(handlerResponse)).Should().Be(await BodyWithoutTraceId(pipelineResponse));

        // The traceId identifies the request (its Activity when one is running, else the connection's request), so it
        // differs between any two responses; everything else must match.
        static async Task<string> BodyWithoutTraceId(HttpResponseMessage response)
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
            body.Remove("traceId");
            return body.ToJsonString();
        }
    }

    [Fact(DisplayName = "DuplicateRequestException maps to 409 with the exception message as the detail")]
    public async Task Duplicate_maps_to_409()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17"));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

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

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(1));
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("detail").GetString().Should().Be("A request with idempotency key 'order-17' is still being processed.");
    }

    [Fact(DisplayName = "IdempotencyKeyMismatchException maps to 422 with the exception message as the detail")]
    public async Task Key_mismatch_maps_to_422()
    {
        await using var host = await StartAsync(() => new IdempotencyKeyMismatchException("order-17"));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(422);
        body.GetProperty("detail").GetString().Should().Be("The idempotency key 'order-17' was already used by a request with a different payload.");
    }

    [Fact(DisplayName = "RateLimitExceededException maps to 429 with a fixed detail (its message names the request type)")]
    public async Task Rate_limit_maps_to_429()
    {
        await using var host = await StartAsync(() => RateLimitException(TimeSpan.FromSeconds(2)));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)429);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().NotContain(nameof(AspNetCoreTestRequestMarker));
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(429);
        body.GetProperty("detail").GetString().Should().Be("Rate limit exceeded. Try again later.");
    }

    [Theory(DisplayName = "The 429's Retry-After is the exception's RetryAfter in whole seconds, rounded up and at least one")]
    [InlineData(29.2, 30)]
    [InlineData(2.0, 2)]
    [InlineData(0.1, 1)]
    public async Task Rate_limit_retry_after_comes_from_the_exception(double retryAfterSeconds, int expectedSeconds)
    {
        await using var host = await StartAsync(() => RateLimitException(TimeSpan.FromSeconds(retryAfterSeconds)));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)429);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // Counted in ticks: one tick past a whole second is another second, however long the delay.
    [Theory(DisplayName = "The 429's Retry-After rounds up the last tick of the exception's RetryAfter")]
    [InlineData(2 * TimeSpan.TicksPerSecond + 1, "3")]
    [InlineData(900_000_000_000 * TimeSpan.TicksPerSecond + 1, "900000000001")]
    [InlineData(long.MaxValue, "922337203686")]
    public async Task Rate_limit_retry_after_rounds_up_to_the_tick(long retryAfterTicks, string expectedHeader)
    {
        await using var host = await StartAsync(() => RateLimitException(TimeSpan.FromTicks(retryAfterTicks)));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)429);
        // Read as sent: a delay beyond an int of seconds does not parse into RetryConditionHeaderValue.
        response.Headers.NonValidated["Retry-After"].ToString().Should().Be(expectedHeader);
    }

    [Fact(DisplayName = "A RateLimitExceededException that does not know its retry-after gets a 429 without Retry-After")]
    public async Task Rate_limit_without_retry_after_sends_no_header()
    {
        await using var host = await StartAsync(() => RateLimitException());

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)429);
        response.Headers.RetryAfter.Should().BeNull();
    }

    [Fact(DisplayName = "End to end: a throttled request's 429 carries the token bucket's own wait")]
    public async Task Throttled_request_carries_the_limiter_wait()
    {
        var limiter = new RequestRateLimiter(
            Options.Create(new RateLimitingOptions { MaxTokens = 1, ReplenishRatePerSecond = 0.25 }),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        limiter.TryAcquire("caller", typeof(AspNetCoreTestRequestMarker), out _).Should().BeTrue();

        await using var host = await StartAsync(() =>
            limiter.TryAcquire("caller", typeof(AspNetCoreTestRequestMarker), out var retryAfter)
                ? new InvalidOperationException("the bucket should be empty")
                : RateLimitException(retryAfter));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be((HttpStatusCode)429);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(4), "a token every four seconds, and none left");
    }

    [Fact(DisplayName = "RequestTimeoutException maps to 504 with a fixed detail (its message names the request type)")]
    public async Task Request_timeout_maps_to_504()
    {
        await using var host = await StartAsync(() => new RequestTimeoutException(typeof(AspNetCoreTestRequestMarker), TimeSpan.FromSeconds(2)));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().NotContain(nameof(AspNetCoreTestRequestMarker));
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(504);
        body.GetProperty("detail").GetString().Should().Be("The request timed out.");
    }

    [Theory(DisplayName = "BackgroundTaskRejectedException maps to 503 with a fixed detail (its message describes the server's queue)")]
    [InlineData(BackgroundTaskRejectionReason.QueueFull)]
    [InlineData(BackgroundTaskRejectionReason.Evicted)]
    [InlineData(BackgroundTaskRejectionReason.QueueClosed)]
    public async Task Rejected_background_work_maps_to_503(BackgroundTaskRejectionReason reason)
    {
        await using var host = await StartAsync(() => new BackgroundTaskRejectedException(reason, "The background task queue is full (500 work items)."));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().NotContain("500 work items");
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(503);
        body.GetProperty("detail").GetString().Should().Be("The server cannot take the request on right now. Try again later.");
        response.Headers.RetryAfter.Should().BeNull("the queue cannot tell when it will have room");
    }

    [Fact(DisplayName = "End to end: a queued dispatch the full queue refuses answers 503")]
    public async Task Queued_dispatch_refused_by_a_full_queue_maps_to_503()
    {
        // No consumer runs, so the first item fills the queue and stays queued.
        using var queue = new BackgroundTaskQueue(Options.Create(new BackgroundTaskQueueOptions { Capacity = 1, FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite }));
        var queued = queue.EnqueueAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken);
        await using var host = await AspNetCoreTestHost.StartAsync(
            services => services.AddCqrsProblemDetails(),
            app =>
            {
                app.UseExceptionHandler();
                app.MapGet("/", async Task<IResult> (CancellationToken ct) =>
                {
                    await queue.EnqueueAsync(_ => Task.CompletedTask, ct);
                    return Results.Ok();
                });
            });

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        queue.CompleteAdding();
        queue.CancelPending();
        await FluentActions.Awaiting(() => queued).Should().ThrowAsync<Exception>("the queued item is withdrawn when the test ends");
    }

    [Fact(DisplayName = "A TimeoutException from a dependency is not handled: the host's default 500 applies")]
    public async Task Dependency_timeout_is_not_handled()
    {
        await using var host = await StartAsync(() => new TimeoutException("connection string Server=internal-db timed out"));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("internal-db");
    }

    [Fact(DisplayName = "TryHandleAsync leaves a dependency's TimeoutException to the host and logs nothing")]
    public async Task Try_handle_returns_false_for_dependency_timeout()
    {
        var logger = new CapturingLogger<CqrsExceptionHandler>();
        var handler = new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()), logger);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        var handled = await handler.TryHandleAsync(context, new TimeoutException("redis"), CancellationToken.None);

        handled.Should().BeFalse();
        logger.Entries.Should().BeEmpty("an exception the handler does not map is the host's to log");
    }

    [Fact(DisplayName = "A mapped exception is logged in one line without the stack trace: at Warning when the server could not serve the request, at Information otherwise")]
    public async Task Mapped_exceptions_are_logged()
    {
        var logger = new CapturingLogger<CqrsExceptionHandler>();
        var handler = new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()), logger);
        var timeout = new RequestTimeoutException(typeof(AspNetCoreTestRequestMarker), TimeSpan.FromSeconds(2));

        (await handler.TryHandleAsync(NewContext(), timeout, CancellationToken.None)).Should().BeTrue();
        (await handler.TryHandleAsync(NewContext(), ValidationException(), CancellationToken.None)).Should().BeTrue();

        logger.Entries.Should().HaveCount(2);
        logger.Entries[0].Should().Match<CapturedLogEntry>(e =>
            e.Level == LogLevel.Warning && e.EventId.Id == 7001 && e.Exception == null && e.Message.Contains("504"));
        logger.Entries[1].Should().Match<CapturedLogEntry>(e =>
            e.Level == LogLevel.Information && e.EventId.Id == 7000 && e.Exception == null && e.Message.Contains("400"));
    }

    [Fact(DisplayName = "The level of a mapped exception follows the exception, not the status code it is configured to map to")]
    public async Task Mapped_exception_level_follows_the_exception()
    {
        var logger = new CapturingLogger<CqrsExceptionHandler>();
        var options = new CqrsProblemDetailsOptions
        {
            TimeoutStatusCode = StatusCodes.Status408RequestTimeout,
            ValidationStatusCode = StatusCodes.Status500InternalServerError
        };
        var handler = new CqrsExceptionHandler(Options.Create(options), logger);

        (await handler.TryHandleAsync(NewContext(), new RequestTimeoutException(typeof(AspNetCoreTestRequestMarker), TimeSpan.FromSeconds(2)), CancellationToken.None))
            .Should().BeTrue();
        (await handler.TryHandleAsync(NewContext(), ValidationException(), CancellationToken.None)).Should().BeTrue();

        logger.Entries.Select(e => (e.EventId.Id, e.Level)).Should().Equal((7001, LogLevel.Warning), (7000, LogLevel.Information));
    }

    [Theory(DisplayName = "The handler logs each outcome the pipeline produces on purpose at the level the logging behavior gives it, neither with the stack trace")]
    [InlineData(PipelineOutcome.Invalid)]
    [InlineData(PipelineOutcome.Duplicate)]
    [InlineData(PipelineOutcome.KeyReused)]
    [InlineData(PipelineOutcome.RateLimited)]
    [InlineData(PipelineOutcome.TimedOut)]
    [InlineData(PipelineOutcome.QueueRefused)]
    public async Task Mapped_exceptions_are_logged_as_the_logging_behavior_logs_them(PipelineOutcome outcome)
    {
        var exception = PipelineOutcomes.Create(outcome);
        var handlerLogger = new CapturingLogger<CqrsExceptionHandler>();
        var behaviorLogger = new CapturingLogger<LoggingBehavior<TestCommand, string>>();

        (await new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()), handlerLogger)
            .TryHandleAsync(NewContext(), exception, CancellationToken.None)).Should().BeTrue();
        var act = () => new LoggingBehavior<TestCommand, string>(behaviorLogger)
            .Handle(new TestCommand(), _ => throw exception, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        var mapped = handlerLogger.Entries.Should().ContainSingle().Subject;
        var outcomeLine = behaviorLogger.Entries.Should().HaveCount(2).And.Subject.Last();
        mapped.Level.Should().Be(outcomeLine.Level);
        mapped.Level.Should().BeOneOf(LogLevel.Information, LogLevel.Warning);
        mapped.Exception.Should().BeNull();
        outcomeLine.Exception.Should().BeNull();
    }

    private static DefaultHttpContext NewContext() => new()
    {
        RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        Response = { Body = new MemoryStream() }
    };

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

        var response = await host.Client.PostAsync("/", content: null, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("detail").GetString().Should().Be("The Idempotency-Key header is required.");
    }

    [Fact(DisplayName = "An unknown exception is not handled: the host's default 500 applies and the message is not written")]
    public async Task Unknown_exception_is_not_handled()
    {
        await using var host = await StartAsync(() => new InvalidOperationException("secret internal state"));

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("secret internal state");
    }

    [Fact(DisplayName = "TryHandleAsync returns false and leaves the response untouched for an unknown exception")]
    public async Task Try_handle_returns_false_for_unknown_exception()
    {
        var handler = new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()), NullLogger<CqrsExceptionHandler>.Instance);
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

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

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

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

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

        var body = await AspNetCoreTestHost.ReadJsonAsync(await host.Client.GetAsync("/", TestContext.Current.CancellationToken));

        body.GetProperty("service").GetString().Should().Be("orders");
        body.GetProperty("exceptionType").GetString().Should().Be(nameof(DuplicateRequestException));
    }

    [Fact(DisplayName = "When no ProblemDetails writer accepts the request (Accept excludes JSON), the problem is still written")]
    public async Task Falls_back_when_problem_details_service_declines()
    {
        await using var host = await StartAsync(() => new DuplicateRequestException("order-17"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AspNetCoreTestHost.ReadJsonAsync(response)).GetProperty("status").GetInt32().Should().Be(409);
    }

    [Fact(DisplayName = "The handler writes the problem itself when no IProblemDetailsService is registered")]
    public async Task Writes_directly_without_problem_details_service()
    {
        var handler = new CqrsExceptionHandler(Options.Create(new CqrsProblemDetailsOptions()), NullLogger<CqrsExceptionHandler>.Instance);
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

        var response = await host.Client.GetAsync("/", TestContext.Current.CancellationToken);

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

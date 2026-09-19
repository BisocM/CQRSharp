using System.Net;
using CQRSharp.AspNetCore;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization.Metadata;

namespace CQRSharp.Tests.Integrations.AspNetCore;

/// <summary>CommandResult / CommandResult&lt;T&gt; to IResult mapping, observed over a real (in-memory) HTTP round trip.</summary>
public sealed class CommandResultHttpExtensionsTests
{
    private sealed record AspNetCoreTestWidget(int Id, string Name);

    private static Task<AspNetCoreTestHost> StartAsync(Func<IResult> endpoint,
        Action<IServiceCollection>? configureServices = null)
    {
        return AspNetCoreTestHost.StartAsync(configureServices, app => app.MapGet("/", endpoint));
    }

    [Fact(DisplayName = "ToHttpResult: a successful CommandResult maps to 204 with no body")]
    public async Task Success_maps_to_204()
    {
        await using var host = await StartAsync(() => CommandResult.FromSuccess().ToHttpResult());

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact(DisplayName = "ToHttpResult: a successful CommandResult<T> maps to 200 carrying the value")]
    public async Task Success_with_value_maps_to_200()
    {
        await using var host = await StartAsync(() =>
            CommandResult<AspNetCoreTestWidget>.FromSuccess(new AspNetCoreTestWidget(7, "gear")).ToHttpResult());

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("id").GetInt32().Should().Be(7);
        body.GetProperty("name").GetString().Should().Be("gear");
    }

    [Fact(DisplayName = "ToHttpResult: a failure maps to a 400 ProblemDetails carrying the message and the error code")]
    public async Task Failure_maps_to_400_problem()
    {
        await using var host = await StartAsync(() => CommandResult.FromError("Out of stock.", 1234).ToHttpResult());

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Bad Request");
        body.GetProperty("detail").GetString().Should().Be("Out of stock.");
        body.GetProperty("errorCode").GetInt32().Should().Be(1234);
    }

    [Fact(DisplayName = "ToHttpResult: a failure without an error code omits the errorCode member")]
    public async Task Failure_without_code_omits_error_code()
    {
        await using var host = await StartAsync(() => CommandResult.FromError("Nope.").ToHttpResult());

        var body = await AspNetCoreTestHost.ReadJsonAsync(await host.Client.GetAsync("/"));

        body.TryGetProperty("errorCode", out _).Should().BeFalse();
    }

    [Fact(DisplayName = "ToHttpResult: the failure status code is overridable, for both result shapes")]
    public async Task Failure_status_is_overridable()
    {
        await using var plain = await StartAsync(() =>
            CommandResult.FromError("Missing.").ToHttpResult(StatusCodes.Status404NotFound));
        await using var typed = await StartAsync(() =>
            CommandResult<AspNetCoreTestWidget>.FromError("Conflict.", 9).ToHttpResult(StatusCodes.Status409Conflict));

        var plainResponse = await plain.Client.GetAsync("/");
        var typedResponse = await typed.Client.GetAsync("/");

        plainResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AspNetCoreTestHost.ReadJsonAsync(plainResponse)).GetProperty("title").GetString().Should().Be("Not Found");
        typedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var typedBody = await AspNetCoreTestHost.ReadJsonAsync(typedResponse);
        typedBody.GetProperty("detail").GetString().Should().Be("Conflict.");
        typedBody.GetProperty("errorCode").GetInt32().Should().Be(9);
    }

    [Fact(DisplayName = "ToCreatedHttpResult: success maps to 201 with the Location header and no body")]
    public async Task Created_without_value()
    {
        await using var host = await StartAsync(() => CommandResult.FromSuccess().ToCreatedHttpResult("/widgets/7"));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.Should().Be("/widgets/7");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact(DisplayName = "ToCreatedHttpResult: CommandResult<T> success maps to 201 with the Location header and the value")]
    public async Task Created_with_value()
    {
        await using var host = await StartAsync(() =>
            CommandResult<AspNetCoreTestWidget>.FromSuccess(new AspNetCoreTestWidget(7, "gear"))
                .ToCreatedHttpResult("/widgets/7"));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.Should().Be("/widgets/7");
        (await AspNetCoreTestHost.ReadJsonAsync(response)).GetProperty("id").GetInt32().Should().Be(7);
    }

    [Fact(DisplayName = "ToCreatedHttpResult: the location factory receives the value, and is not invoked on failure")]
    public async Task Created_with_location_factory()
    {
        var factoryCalledOnFailure = false;
        await using var success = await StartAsync(() =>
            CommandResult<AspNetCoreTestWidget>.FromSuccess(new AspNetCoreTestWidget(42, "cog"))
                .ToCreatedHttpResult(widget => $"/widgets/{widget.Id}"));
        await using var failure = await StartAsync(() =>
            CommandResult<AspNetCoreTestWidget>.FromError("Rejected.").ToCreatedHttpResult(_ =>
            {
                factoryCalledOnFailure = true;
                return "/never";
            }));

        var successResponse = await success.Client.GetAsync("/");
        var failureResponse = await failure.Client.GetAsync("/");

        successResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        successResponse.Headers.Location!.OriginalString.Should().Be("/widgets/42");
        failureResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        failureResponse.Headers.Location.Should().BeNull();
        factoryCalledOnFailure.Should().BeFalse();
    }

    [Fact(DisplayName = "ToCreatedHttpResult: a failure maps to a ProblemDetails with the overridden status")]
    public async Task Created_failure_maps_to_problem()
    {
        await using var host = await StartAsync(() =>
            CommandResult.FromError("Already exists.", 5).ToCreatedHttpResult("/widgets/7", StatusCodes.Status409Conflict));

        var response = await host.Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await AspNetCoreTestHost.ReadJsonAsync(response);
        body.GetProperty("detail").GetString().Should().Be("Already exists.");
        body.GetProperty("errorCode").GetInt32().Should().Be(5);
    }

    [Fact(DisplayName = "ToHttpResult: the failure body serializes with reflection-based JSON removed (the Native AOT configuration)")]
    public async Task Failure_serializes_without_reflection_resolver()
    {
        await using var host = await StartAsync(
            () => CommandResult.FromError("Out of stock.", 1234).ToHttpResult(),
            services =>
            {
                services.AddProblemDetails();
                // Emulates a Native AOT app: only source-generated JSON metadata (the framework's ProblemDetails
                // context) remains, so an extension value the context cannot serialize would fail here.
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
        (await AspNetCoreTestHost.ReadJsonAsync(response)).GetProperty("errorCode").GetInt32().Should().Be(1234);
    }

    [Fact(DisplayName = "ToHttpResult: the host's CustomizeProblemDetails applies to failure responses")]
    public async Task Host_problem_details_customization_applies()
    {
        await using var host = await StartAsync(
            () => CommandResult.FromError("Out of stock.").ToHttpResult(),
            services => services.AddProblemDetails(o =>
                o.CustomizeProblemDetails = context => context.ProblemDetails.Extensions["service"] = "orders"));

        var body = await AspNetCoreTestHost.ReadJsonAsync(await host.Client.GetAsync("/"));

        body.GetProperty("detail").GetString().Should().Be("Out of stock.");
        body.GetProperty("service").GetString().Should().Be("orders");
    }

    [Fact(DisplayName = "Null arguments are rejected")]
    public void Null_arguments_are_rejected()
    {
        var success = CommandResult.FromSuccess();
        var typed = CommandResult<int>.FromSuccess(1);

        FluentActions.Invoking(() => ((CommandResult)null!).ToHttpResult()).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ((CommandResult<int>)null!).ToHttpResult()).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => success.ToCreatedHttpResult(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => typed.ToCreatedHttpResult((string)null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => typed.ToCreatedHttpResult((Func<int, string>)null!)).Should().Throw<ArgumentNullException>();
    }
}

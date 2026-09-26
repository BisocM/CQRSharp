using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CQRSharp.AspNetCore;
using CQRSharp.Sample.AspNetCore.Orders;

namespace CQRSharp.Sample.AspNetCore.SelfTest;

/// <summary>
///     Calls every endpoint of the running app over HTTP, as a client would, and checks each response: its status, its
///     headers and its body.
/// </summary>
internal sealed partial class HttpSelfTest(HttpClient client)
{
    private const string ProblemJson = "application/problem+json";

    private Guid _orderId;

    public static async Task<bool> RunAsync(Uri baseAddress, ILogger logger, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = baseAddress };
        var test = new HttpSelfTest(client);

        (string Name, Func<CancellationToken, Task> Run)[] scenarios =
        [
            ("place an order, replay it, reuse its key", test.PlaceOrderAsync),
            ("reject a missing Idempotency-Key", test.RejectMissingIdempotencyKeyAsync),
            ("reject an invalid order", test.RejectInvalidOrderAsync),
            ("read an order", test.ReadOrderAsync),
            ("rate limit order lookups", test.RateLimitLookupsAsync),
            ("cancel an order", test.CancelOrderAsync)
        ];

        LogStarting(logger, baseAddress);
        var current = string.Empty;
        try
        {
            foreach (var (name, run) in scenarios)
            {
                current = name;
                LogScenario(logger, name);
                await run(cancellationToken);
            }

            LogPassed(logger, scenarios.Length);
            return true;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, current);
            return false;
        }
    }

    private async Task PlaceOrderAsync(CancellationToken cancellationToken)
    {
        var key = $"order-{Guid.NewGuid():N}";

        using var placed = await PostOrderAsync(new PlaceOrderBody("A-1", 2), key, cancellationToken);
        Require(placed.StatusCode == HttpStatusCode.Created, $"POST /orders answered {placed.StatusCode}, not 201.");
        _orderId = await ReadGuidAsync(placed, cancellationToken);
        Require(placed.Headers.Location?.OriginalString == $"/orders/{_orderId}",
            $"POST /orders answered Location '{placed.Headers.Location}' for order {_orderId}.");

        // The retry is answered with the original result, replayed from the idempotency store.
        using var replayed = await PostOrderAsync(new PlaceOrderBody("A-1", 2), key, cancellationToken);
        Require(replayed.StatusCode == HttpStatusCode.Created, $"The retried POST answered {replayed.StatusCode}, not 201.");
        var replayedId = await ReadGuidAsync(replayed, cancellationToken);
        Require(replayedId == _orderId, $"The retried POST placed order {replayedId} instead of replaying order {_orderId}.");

        using var reused = await PostOrderAsync(new PlaceOrderBody("A-1", 3), key, cancellationToken);
        using var _ = await RequireProblemAsync(reused, HttpStatusCode.UnprocessableEntity, cancellationToken);
    }

    private async Task RejectMissingIdempotencyKeyAsync(CancellationToken cancellationToken)
    {
        using var missing = await PostOrderAsync(new PlaceOrderBody("A-1", 1), idempotencyKey: null, cancellationToken);
        using var problem = await RequireProblemAsync(missing, HttpStatusCode.BadRequest, cancellationToken);
        Require(problem.RootElement.GetProperty("detail").GetString()?.Contains(IdempotencyKeyHttpExtensions.HeaderName) == true,
            "The missing-key problem does not name the Idempotency-Key header.");

        // A client that accepts no JSON still gets the problem: the handler writes it itself when no writer takes it.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new PlaceOrderBody("A-1", 1), SampleJsonContext.Default.PlaceOrderBody)
        };
        request.Headers.Accept.ParseAdd("text/plain");
        using var plain = await client.SendAsync(request, cancellationToken);
        using var _ = await RequireProblemAsync(plain, HttpStatusCode.BadRequest, cancellationToken);
    }

    private async Task RejectInvalidOrderAsync(CancellationToken cancellationToken)
    {
        using var invalid = await PostOrderAsync(new PlaceOrderBody("", 0), $"order-{Guid.NewGuid():N}", cancellationToken);
        using var problem = await RequireProblemAsync(invalid, HttpStatusCode.BadRequest, cancellationToken);

        var errors = problem.RootElement.GetProperty("errors");
        Require(errors.TryGetProperty(nameof(PlaceOrder.Sku), out _) && errors.TryGetProperty(nameof(PlaceOrder.Quantity), out _),
            $"The validation problem's errors are {errors}, not one for Sku and one for Quantity.");
        var quantityCodes = problem.RootElement.GetProperty("errorCodes").GetProperty(nameof(PlaceOrder.Quantity));
        Require(quantityCodes is { ValueKind: JsonValueKind.Array } && quantityCodes[0].GetString() == "QUANTITY_RANGE",
            $"The validation problem's error codes for Quantity are {quantityCodes}.");
    }

    private async Task ReadOrderAsync(CancellationToken cancellationToken)
    {
        using var found = await client.GetAsync($"/orders/{_orderId}", cancellationToken);
        Require(found.StatusCode == HttpStatusCode.OK, $"GET /orders/{{id}} answered {found.StatusCode}, not 200.");
        var order = await found.Content.ReadFromJsonAsync(SampleJsonContext.Default.OrderDto, cancellationToken);
        Require(order == new OrderDto(_orderId, "A-1", 2, OrderStatus.Placed), $"GET /orders/{{id}} returned {order}.");

        using var missing = await client.GetAsync($"/orders/{Guid.NewGuid()}", cancellationToken);
        Require(missing.StatusCode == HttpStatusCode.NotFound, $"GET of an unknown order answered {missing.StatusCode}, not 404.");
    }

    private async Task RateLimitLookupsAsync(CancellationToken cancellationToken)
    {
        // Lookups share one bucket per caller: keep reading until it is empty.
        var retryAfter = TimeSpan.Zero;
        for (var lookups = 0; retryAfter == TimeSpan.Zero; lookups++)
        {
            Require(lookups < 10, "Ten order lookups in a row were not rate limited.");
            using var response = await client.GetAsync($"/orders/{_orderId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                retryAfter = await RequireRateLimitedAsync(response, cancellationToken);
            else
                Require(response.StatusCode == HttpStatusCode.OK, $"GET /orders/{{id}} answered {response.StatusCode}, not 200 or 429.");
        }

        // What a well-behaved client does: wait as long as Retry-After says, and again if the server says it is still early.
        for (var attempt = 1;; attempt++)
        {
            await Task.Delay(retryAfter, cancellationToken);
            using var response = await client.GetAsync($"/orders/{_orderId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK) return;

            Require(response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3,
                $"The lookup after waiting out Retry-After answered {response.StatusCode}, not 200.");
            retryAfter = await RequireRateLimitedAsync(response, cancellationToken);
        }
    }

    private async Task CancelOrderAsync(CancellationToken cancellationToken)
    {
        using var cancelled = await client.DeleteAsync($"/orders/{_orderId}", cancellationToken);
        Require(cancelled.StatusCode == HttpStatusCode.NoContent, $"DELETE /orders/{{id}} answered {cancelled.StatusCode}, not 204.");

        using var again = await client.DeleteAsync($"/orders/{_orderId}", cancellationToken);
        using (var conflict = await RequireProblemAsync(again, HttpStatusCode.Conflict, cancellationToken))
            RequireErrorKind(conflict, CommandErrorKind.Conflict);

        using var unknown = await client.DeleteAsync($"/orders/{Guid.NewGuid()}", cancellationToken);
        using (var notFound = await RequireProblemAsync(unknown, HttpStatusCode.NotFound, cancellationToken))
            RequireErrorKind(notFound, CommandErrorKind.NotFound);
    }

    private Task<HttpResponseMessage> PostOrderAsync(PlaceOrderBody body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(body, SampleJsonContext.Default.PlaceOrderBody)
        };
        if (idempotencyKey is not null) request.Headers.Add(IdempotencyKeyHttpExtensions.HeaderName, idempotencyKey);

        return SendAndDisposeRequestAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
            return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<Guid> ReadGuidAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync(SampleJsonContext.Default.Guid, cancellationToken);

    // A failure is a ProblemDetails document whose status is the response's own.
    private static async Task<JsonDocument> RequireProblemAsync(HttpResponseMessage response, HttpStatusCode status, CancellationToken cancellationToken)
    {
        var method = response.RequestMessage?.Method;
        var path = response.RequestMessage?.RequestUri?.AbsolutePath;
        Require(response.StatusCode == status, $"{method} {path} answered {(int)response.StatusCode}, not {(int)status}.");
        Require(response.Content.Headers.ContentType?.MediaType == ProblemJson,
            $"{method} {path} answered {response.Content.Headers.ContentType}, not {ProblemJson}.");

        var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        Require(problem.RootElement.TryGetProperty("status", out var body) && body.GetInt32() == (int)status,
            $"{method} {path}'s problem document does not carry status {(int)status}.");
        return problem;
    }

    private static async Task<TimeSpan> RequireRateLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var _ = await RequireProblemAsync(response, HttpStatusCode.TooManyRequests, cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta;
        Require(retryAfter >= TimeSpan.FromSeconds(1), $"The 429 carried Retry-After '{response.Headers.RetryAfter}', not a delay in seconds.");
        return retryAfter!.Value;
    }

    private static void RequireErrorKind(JsonDocument problem, CommandErrorKind kind)
        => Require(problem.RootElement.TryGetProperty(CommandResultHttpExtensions.ErrorKindExtensionName, out var value) &&
                   value.GetString() == kind.ToString(),
            $"The problem document does not carry {CommandResultHttpExtensions.ErrorKindExtensionName} '{kind}'.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [LoggerMessage(9100, LogLevel.Information, "Self-test starting against {BaseAddress}")]
    private static partial void LogStarting(ILogger logger, Uri baseAddress);

    [LoggerMessage(9101, LogLevel.Information, "Scenario {Scenario}")]
    private static partial void LogScenario(ILogger logger, string scenario);

    [LoggerMessage(9102, LogLevel.Information, "Self-test passed: {Count} scenarios")]
    private static partial void LogPassed(ILogger logger, int count);

    [LoggerMessage(9103, LogLevel.Critical, "Self-test failed in scenario {Scenario}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string scenario);
}

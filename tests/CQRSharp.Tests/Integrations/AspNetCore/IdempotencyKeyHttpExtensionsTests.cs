using System.Net;
using CQRSharp.AspNetCore;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace CQRSharp.Tests.Integrations.AspNetCore;

/// <summary>Idempotency-Key header reading and validation.</summary>
public sealed class IdempotencyKeyHttpExtensionsTests
{
    private static DefaultHttpContext ContextWith(params string[] headerValues)
    {
        var context = new DefaultHttpContext();
        if (headerValues.Length > 0)
            context.Request.Headers[IdempotencyKeyHttpExtensions.HeaderName] = new StringValues(headerValues);
        return context;
    }

    [Theory(DisplayName = "A valid key is returned as sent, minus surrounding whitespace and one pair of quotes")]
    [InlineData("8e03978e-40d5-43e8-bc93-6894a57f9324", "8e03978e-40d5-43e8-bc93-6894a57f9324")]
    [InlineData("  order-17  ", "order-17")]
    [InlineData("\"order-17\"", "order-17")]
    [InlineData("Order:17/A_b.c~", "Order:17/A_b.c~")]
    public void Valid_key_is_returned(string header, string expected)
    {
        var context = ContextWith(header);

        context.TryGetIdempotencyKey(out var key).Should().BeTrue();
        key.Should().Be(expected);
        context.GetIdempotencyKey().Should().Be(expected);
    }

    [Theory(DisplayName = "A malformed key is rejected")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("has space")]
    [InlineData("a,b")]
    [InlineData("quo\"te")]
    [InlineData("back\\slash")]
    [InlineData("tab\tkey")]
    [InlineData("clé")]
    public void Malformed_key_is_rejected(string header)
    {
        var context = ContextWith(header);

        context.TryGetIdempotencyKey(out var key).Should().BeFalse();
        key.Should().BeNull();
        context.Invoking(c => c.GetIdempotencyKey()).Should().Throw<InvalidIdempotencyKeyException>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact(DisplayName = "A missing header is rejected with a 'required' message")]
    public void Missing_header_is_rejected()
    {
        var context = ContextWith();

        context.TryGetIdempotencyKey(out _).Should().BeFalse();
        context.Invoking(c => c.GetIdempotencyKey()).Should().Throw<InvalidIdempotencyKeyException>()
            .WithMessage("The Idempotency-Key header is required.");
    }

    [Fact(DisplayName = "A header sent more than once is rejected as ambiguous")]
    public void Repeated_header_is_rejected()
    {
        var context = ContextWith("key-1", "key-2");

        context.TryGetIdempotencyKey(out _).Should().BeFalse();
        context.Invoking(c => c.GetIdempotencyKey()).Should().Throw<InvalidIdempotencyKeyException>()
            .WithMessage("*exactly once*");
    }

    [Fact(DisplayName = "The default maximum length is 255 and the limit is inclusive")]
    public void Default_max_length_is_enforced()
    {
        ContextWith(new string('k', 255)).TryGetIdempotencyKey(out _).Should().BeTrue();
        ContextWith(new string('k', 256)).TryGetIdempotencyKey(out _).Should().BeFalse();
    }

    [Fact(DisplayName = "The maximum length is configurable, and the rejection message does not echo the key")]
    public void Max_length_is_configurable()
    {
        ContextWith("12345678").TryGetIdempotencyKey(out _, maxLength: 8).Should().BeTrue();
        ContextWith("123456789").TryGetIdempotencyKey(out _, maxLength: 8).Should().BeFalse();
        ContextWith(new string('k', 400)).TryGetIdempotencyKey(out _, maxLength: 512).Should().BeTrue();

        ContextWith("123456789").Invoking(c => c.GetIdempotencyKey(maxLength: 8))
            .Should().Throw<InvalidIdempotencyKeyException>()
            .Which.Message.Should().Be("The Idempotency-Key header must not exceed 8 characters.");
    }

    [Fact(DisplayName = "Invalid arguments are rejected")]
    public void Invalid_arguments_are_rejected()
    {
        FluentActions.Invoking(() => ((HttpContext)null!).TryGetIdempotencyKey(out _)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ((HttpContext)null!).GetIdempotencyKey()).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ContextWith("k").TryGetIdempotencyKey(out _, maxLength: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "The header is read from a real request, case-insensitively")]
    public async Task Header_is_read_from_a_real_request()
    {
        await using var host = await AspNetCoreTestHost.StartAsync(null, app =>
            app.MapPost("/", (HttpContext context) =>
                context.TryGetIdempotencyKey(out var key) ? Results.Text(key) : Results.BadRequest()));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.TryAddWithoutValidation("idempotency-key", "order-17");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("order-17");
    }
}

using CQRSharp.FluentValidation;
using CQRSharp.Pipelines;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using CqrsFailure = CQRSharp.ValidationFailure;

namespace CQRSharp.Tests.Integrations.FluentValidation;

/// <summary>
///     End-to-end: FluentValidation validators registered in DI reject a request dispatched through the generated
///     wiring, compose with native validators, and stay out of the way of requests that have none.
/// </summary>
public sealed class FluentValidationPipelineTests
{
    [Fact(DisplayName = "End-to-end: UseValidation + AddCqrsFluentValidation rejects an invalid request with the mapped failures")]
    public async Task Invalid_request_throws_with_mapped_failures()
    {
        await using var provider = Build(
            b => b.UseValidation(),
            services =>
            {
                services.AddCqrsFluentValidation();
                services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>();
            });
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "", Quantity = 0 });

        var exception = (await act.Should().ThrowAsync<RequestValidationException>()).Which;
        exception.RequestType.Should().Be<FvOrderQuery>();
        exception.Failures.Should().Equal(
            new CqrsFailure("NAME_REQUIRED", "A name is required.", nameof(FvOrderQuery.Name)),
            new CqrsFailure("QUANTITY_POSITIVE", "Quantity must be positive.", nameof(FvOrderQuery.Quantity)));
    }

    [Fact(DisplayName = "End-to-end: a valid request reaches the handler")]
    public async Task Valid_request_reaches_the_handler()
    {
        await using var provider = Build(
            b => b.UseFluentValidation(),
            services => services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>());
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "widget", Quantity = 2 }, TestContext.Current.CancellationToken);

        result.Should().Be(new FvOrderResult("widget x2"));
    }

    [Fact(DisplayName = "End-to-end: UseFluentValidation() alone is enough: the builder's default validation behavior runs the adapter")]
    public async Task Builder_verb_runs_under_the_default_validation_behavior()
    {
        await using var provider = Build(
            b => b.UseFluentValidation(),
            services => services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>());
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "widget", Quantity = 0 });

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Failures
            .Should().ContainSingle().Which.Code.Should().Be("QUANTITY_POSITIVE");
    }

    [Theory(DisplayName = "End-to-end: UseValidation(false) turns FluentValidation validators off whichever of the two verbs comes first")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validation_opt_out_wins_in_either_order(bool optOutFirst)
    {
        await using var provider = Build(
            b =>
            {
                if (optOutFirst) b.UseValidation(false).UseFluentValidation();
                else b.UseFluentValidation().UseValidation(false);
            },
            services => services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>());
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "", Quantity = 0 }, TestContext.Current.CancellationToken);

        result.Should().Be(new FvOrderResult(" x0"), "with the validation behavior off no validator runs");
    }

    [Fact(DisplayName = "End-to-end: a request with no FluentValidation validator passes untouched")]
    public async Task Request_without_validator_passes()
    {
        await using var provider = Build(
            b => b.UseFluentValidation(),
            services => services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>());
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new FvUnvalidatedQuery(), TestContext.Current.CancellationToken);

        result.Should().Be(new FvUnvalidatedResult(true));
    }

    [Fact(DisplayName = "End-to-end: several FluentValidation validators for one request all run")]
    public async Task Multiple_registered_validators_combine()
    {
        await using var provider = Build(
            b => b.UseFluentValidation(),
            services =>
            {
                services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>();
                services.AddScoped<IValidator<FvOrderQuery>, OrderNameLengthValidator>();
            });
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "ab", Quantity = 0 });

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Failures.Select(f => f.Code)
            .Should().Equal("QUANTITY_POSITIVE", "NAME_TOO_SHORT");
    }

    [Fact(DisplayName = "End-to-end: a native IRequestValidator and a FluentValidation validator both run and their failures combine")]
    public async Task Composes_with_native_validators()
    {
        await using var provider = Build(
            b => b.UseFluentValidation(),
            services =>
            {
                services.AddTransient<IRequestValidator<FvOrderQuery>, NativeOrderValidator>();
                services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>();
            });
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "forbidden", Quantity = 0 });

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Failures.Should().BeEquivalentTo(
        [
            new CqrsFailure("QUANTITY_POSITIVE", "Quantity must be positive.", nameof(FvOrderQuery.Quantity)),
            new CqrsFailure("NAME_FORBIDDEN", "That name is not allowed.", nameof(FvOrderQuery.Name))
        ]);
    }

    [Fact(DisplayName = "Registration: the adapter is registered once however many times the verbs are called")]
    public void Registration_is_idempotent()
    {
        var services = new ServiceCollection();

        services.AddCqrsFluentValidation();
        services.AddCqrsFluentValidation();
        services.AddCqrsGenerated(b => b.UseFluentValidation().UseFluentValidation());

        services.Where(d => d.ImplementationType == typeof(FluentValidationRequestValidator<>))
            .Should().ContainSingle()
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.ServiceType == typeof(IRequestValidator<>) && d.Lifetime == ServiceLifetime.Transient);
    }

    [Fact(DisplayName = "Registration: without the adapter, FluentValidation validators in DI are not consulted")]
    public async Task Without_the_adapter_validators_are_inert()
    {
        await using var provider = Build(
            b => b.UseValidation(),
            services => services.AddScoped<IValidator<FvOrderQuery>, OrderValidator>());
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new FvOrderQuery { Name = "", Quantity = 0 }, TestContext.Current.CancellationToken);

        result.Should().Be(new FvOrderResult(" x0"));
    }

    private static ServiceProvider Build(Action<ICqrsBuilder> configureCqrs, Action<IServiceCollection> configureServices)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(configureCqrs);
        configureServices(services);
        // Scope validation on, so a lifetime mistake in the adapter (capturing scoped validators from the root) fails here.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class OrderValidator : AbstractValidator<FvOrderQuery>
    {
        public OrderValidator()
        {
            RuleFor(q => q.Name).NotEmpty().WithErrorCode("NAME_REQUIRED").WithMessage("A name is required.");
            RuleFor(q => q.Quantity).GreaterThan(0).WithErrorCode("QUANTITY_POSITIVE").WithMessage("Quantity must be positive.");
        }
    }

    private sealed class OrderNameLengthValidator : AbstractValidator<FvOrderQuery>
    {
        public OrderNameLengthValidator()
        {
            RuleFor(q => q.Name).MinimumLength(3).WithErrorCode("NAME_TOO_SHORT");
        }
    }

    // Private, so the generator does not auto-register it for the whole test assembly; the one test that wants it
    // registers it by hand.
    private sealed class NativeOrderValidator : IRequestValidator<FvOrderQuery>
    {
        public Task<CqrsFailure[]> ValidateAsync(FvOrderQuery request, CancellationToken cancellationToken)
        {
            return Task.FromResult(request.Name == "forbidden"
                ? new[] { new CqrsFailure("NAME_FORBIDDEN", "That name is not allowed.", nameof(request.Name)) }
                : Array.Empty<CqrsFailure>());
        }
    }
}

public sealed record FvOrderResult(string Summary);

// Its own request and result types, so nothing here can alter how any other fixture in the assembly is validated.
public sealed class FvOrderQuery : QueryBase<FvOrderResult>
{
    public string Name { get; init; } = string.Empty;

    public int Quantity { get; init; }
}

public sealed class FvOrderQueryHandler : IQueryHandler<FvOrderQuery, FvOrderResult>
{
    public Task<FvOrderResult> Handle(FvOrderQuery query, CancellationToken cancellationToken)
    {
        return Task.FromResult(new FvOrderResult($"{query.Name} x{query.Quantity}"));
    }
}

public sealed record FvUnvalidatedResult(bool Handled);

public sealed class FvUnvalidatedQuery : QueryBase<FvUnvalidatedResult>;

public sealed class FvUnvalidatedQueryHandler : IQueryHandler<FvUnvalidatedQuery, FvUnvalidatedResult>
{
    public Task<FvUnvalidatedResult> Handle(FvUnvalidatedQuery query, CancellationToken cancellationToken)
    {
        return Task.FromResult(new FvUnvalidatedResult(true));
    }
}

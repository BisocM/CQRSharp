using System.Text.Json;
using CQRSharp.Sample.Application.Pipelines;
using CQRSharp.Sample.Infrastructure.Identity;
using CQRSharp.Sample.Infrastructure.Persistence;
using CQRSharp.Sample.Infrastructure.SelfTest;
using CQRSharp.Sample.Infrastructure.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Presentation;

/// <summary>The sample application's services, in one place so the self-test can build variants of the same app.</summary>
public static class SampleApplication
{
    /// <summary>
    ///     What the Development environment turns on: building the container fails if a registration cannot be
    ///     constructed, and resolving a scoped service from the root (or from a singleton) throws.
    /// </summary>
    public static ServiceProviderOptions ValidatingProviderOptions => new() { ValidateOnBuild = true, ValidateScopes = true };

    public static IServiceCollection AddSampleApplication(this IServiceCollection services)
    {
        // Verb order is irrelevant: the builder applies every verb in one fixed sequence.
        services.AddCqrsGenerated(b => b
            .UseLogging()
            .UseOutbox(o => o
                .Transactional()
                .UseInMemoryStore()
                .ConfigureProcessor(p => p.PollingInterval = TimeSpan.FromMilliseconds(100)))
            .UseUnitOfWork(provider => new InMemoryUnitOfWork(provider.GetRequiredService<ILogger<InMemoryUnitOfWork>>()))
            .UseRateLimiting(o =>
            {
                o.MaxTokens = 5;
                o.ReplenishRatePerSecond = 1;
                o.Scope = RateLimitScope.PerRequestType;
            })
            .UseIdempotency(i => i
                .UseInMemoryStore()
                .ReplayResultsWith(new JsonSerializerOptions { TypeInfoResolver = SampleJsonContext.Default }))
            .UseTimeout(o => o.Timeout = TimeSpan.FromSeconds(2))
            .UseResilience(o =>
            {
                o.MaxRetries = 2;
                o.BaseDelay = TimeSpan.Zero;
            })
            .ValidateOnStart());

        // Registered after AddCqrsGenerated, the MediatR-style order. Under Native AOT these still wrap the requests with a
        // value-type result (AddNumbersQuery, CountdownStreamRequest) and the value-type notification
        // (SensorReadingNotification), through the closed factories the generator emits.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RequestCountingBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamProbeBehavior<,>));
        services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(NotificationLoggingBehavior<>));

        services.AddScoped<CurrentUser>();
        services.AddSingleton<CustomInMemoryUserStore>();

        // The self-test's own services: what the handlers observed, and a scoped service whose id identifies its scope.
        services.AddSingleton<SampleDiagnostics>();
        services.AddScoped<SampleScopedMarker>();
        services.AddSingleton<SelfTestRunner>();

        // Handlers, the validator, the exception hooks and CustomRequestContextFactory need no registration: the source
        // generator registers every one it finds.
        return services;
    }
}

using CQRSharp.Abstractions.Interfaces.Validation;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications.Pipelines;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Extensions;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Application.Pipelines;
using CQRSharp.Sample.Application.Validation;
using CQRSharp.Sample.Infrastructure.Factories;
using CQRSharp.Sample.Infrastructure.Persistence;
using CQRSharp.Sample.Infrastructure.SelfTest;
using CQRSharp.Sample.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Presentation;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((_, services) =>
            {
                // The fluent builder wires CQRSharp in one call. Verb order is irrelevant — the same registrations
                // and the same resolved pipeline result whatever order these are written in. UseInMemoryOutbox()
                // registers the production in-process store shipped in CQRSharp.Core (replacing a hand-rolled demo
                // store); ValidateOnStart() keeps the fail-fast startup validator active.
                services.AddCqrsGenerated(b => b
                    .ConfigureQueue(opts => opts.EnableMetrics = true)
                    // One cohesive verb: transactional mode + in-memory store + processor tuning, instead of a mode
                    // flag plus a separately-registered store plus a raw services.Configure<OutboxProcessorOptions>.
                    .UseOutbox(o => o
                        .Transactional()
                        .UseInMemoryStore()
                        .ConfigureProcessor(p => p.PollingInterval = TimeSpan.FromMilliseconds(100)))
                    .UseUnitOfWork(provider => new InMemoryUnitOfWork(
                        provider.GetRequiredService<ILogger<InMemoryUnitOfWork>>(),
                        provider))
                    .UseRateLimiting(options =>
                    {
                        options.MaxTokens = 2;
                        options.ReplenishRatePerSecond = 1;
                        options.Scope = RateLimitScope.PerCommand;
                    })
                    .UseTimeout(options => options.Timeout = TimeSpan.FromSeconds(2))
                    .UseResilience(options =>
                    {
                        options.MaxRetries = 2;
                        options.BaseDelay = TimeSpan.Zero;
                    })
                    .ValidateOnStart());

                // Backstop for the NativeAOT publish workflow: referencing and registering the Redis integration pulls
                // its IL into the AOT-published output (the workflow greps for it). It is never actually used —
                // UseInMemoryOutbox() above already won the IOutboxStore slot (both register via TryAdd), and
                // abortConnect=false with a lazy multiplexer factory means nothing connects at startup or runtime.
                services.AddRedisOutboxStore("127.0.0.1:6379,abortConnect=false");

                services.AddSingleton<SampleUserContext>();
                services.AddSingleton<SampleDiagnostics>();
                services.AddScoped<SampleScopedMarker>();

                services.AddSingleton<CustomInMemoryUserStore>();

                services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingPipelineBehavior<,>));
                services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(NotificationLoggingBehavior<>));

                services.AddTransient<IRequestValidator<ValidatedCommand>, ValidatedCommandValidator>();

                services.AddTransient<IRequestContextFactory<SampleRequestContext>, CustomRequestContextFactory>();
                services.AddHostedService<SampleHostedService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        await host.RunAsync();
    }
}

// CQRSharp.Sample/Program.cs

	using CQRSharp.Core.Extensions;
	using CQRSharp.Core.Factories;
	using CQRSharp.Abstractions.Data.Interfaces.Outbox;
	using CQRSharp.Core.Options.Enums;
	using CQRSharp.Core.Pipelines;
	using CQRSharp.Pipelines.Extensions;
	using CQRSharp.Pipelines.Types.RateLimiting;
	using CQRSharp.Sample.Application.Contexts;
using CQRSharp.Sample.Application.Pipelines;
using CQRSharp.Sample.Infrastructure.Factories;
using CQRSharp.Sample.Infrastructure.Persistence;
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
                // Core CQRSharp services
                services.AddCqrsGenerated(
                        opts => { opts.EnableMetrics = true; },
                        opts => { opts.Mode = OutboxMode.Transactional; });

	                // Register singleton data stores for the sample
	                services.AddSingleton<CustomInMemoryUserStore>();
	                services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();

                // --- PIPELINE BEHAVIORS ---
                services
                    // 1. Custom logging pipeline (demonstrates custom logic, sensitive data redaction)
                    .AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingPipelineBehavior<,>))
                    // 2. Unit of Work (handles transactions and outbox integration)
                    .AddUnitOfWorkBehavior(provider => new InMemoryUnitOfWork(
                        provider.GetRequiredService<ILogger<InMemoryUnitOfWork>>(),
                        provider))
                    // 3. Rate Limiting
                    .AddRateLimiting(options =>
	                    {
	                        options.MaxTokens = 2;
	                        options.ReplenishRatePerSecond = 1;
	                        options.Scope = RateLimitScope.PerCommand;
	                    })
                    // 4. Timeout
                    .AddTimeoutBehavior(o => { o.Timeout = TimeSpan.FromSeconds(2); })
                    // 5. Resilience (retries)
                    .AddResilienceBehavior(o => { o.MaxRetries = 2; });

                // Add background processor for the outbox
                services.AddOutboxProcessor(opts =>
                {
                    opts.PollingInterval = TimeSpan.FromSeconds(2);
                    opts.BatchSize = 10;
                });

                // Register a custom factory for our request context
                services.AddTransient<IRequestContextFactory<SampleRequestContext>, CustomRequestContextFactory>();

                // Add the main application service
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

using CQRSharp.Abstractions.Interfaces.Outbox;
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
                services.AddCqrsGenerated(
                    opts => { opts.EnableMetrics = true; },
                    opts => { opts.Mode = OutboxMode.Transactional; });

                services.AddSingleton<SampleUserContext>();
                services.AddSingleton<SampleDiagnostics>();
                services.AddScoped<SampleScopedMarker>();

                services.AddSingleton<CustomInMemoryUserStore>();

                services.AddSingleton<InMemoryOutboxStore>();
                services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());

                services.Configure<OutboxProcessorOptions>(opts => { opts.PollingInterval = TimeSpan.FromMilliseconds(100); });

                services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingPipelineBehavior<,>));
                services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(NotificationLoggingBehavior<>));

                services.AddTransient<IRequestValidator<ValidatedCommand>, ValidatedCommandValidator>();

                services.AddCqrsPipelinePack(pack =>
                {
                    pack.UnitOfWorkFactory = provider => new InMemoryUnitOfWork(
                        provider.GetRequiredService<ILogger<InMemoryUnitOfWork>>(),
                        provider);

                    pack.ConfigureRateLimiting = options =>
                    {
                        options.MaxTokens = 2;
                        options.ReplenishRatePerSecond = 1;
                        options.Scope = RateLimitScope.PerCommand;
                    };

                    pack.ConfigureTimeout = options => { options.Timeout = TimeSpan.FromSeconds(2); };
                    pack.ConfigureResilience = options =>
                    {
                        options.MaxRetries = 2;
                        options.BaseDelay = TimeSpan.Zero;
                    };
                });

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
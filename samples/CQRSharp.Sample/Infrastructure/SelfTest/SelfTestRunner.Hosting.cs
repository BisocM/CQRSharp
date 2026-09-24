using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Persistence;
using CQRSharp.Redis;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Application.Queries.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.Identity;
using CQRSharp.Sample.Presentation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CQRSharp.Sample.Infrastructure.SelfTest;

// What runs beside dispatch: the background queue, the transactional outbox and its processor, and two variants of the
// same application built next to this one (queued dispatch; the Redis stores).
public sealed partial class SelfTestRunner
{
    private static async Task RunBackgroundQueueTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var queue = scenario.Services.GetRequiredService<IBackgroundTaskManager>();

        var result = await queue.EnqueueAsync(_ => Task.FromResult(42), cancellationToken);

        Require(result == 42, $"The background queue returned {result}.");
    }

    private async Task RunTransactionalOutboxTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var userId = Guid.NewGuid();
        var command = new CreateUserCommand("Ada", userId);

        var result = await scenario.Cqrs.Send(command, cancellationToken);

        Require(result.IsSuccess, "CreateUserCommand did not succeed.");
        Require(diagnostics.GetRequestCount(typeof(CreateUserCommand)) > 0, "RequestCountingBehavior did not wrap CreateUserCommand.");
        Require(command.Context?.UserId == scenario.UserId,
            $"CreateUserCommand's context names user '{command.Context?.UserId}', not the scope's '{scenario.UserId}'.");

        // The lifecycle notifications are published in-process during the dispatch, so they have run by now.
        RequireNotificationPipelineExecuted(typeof(CommandInitiatedNotification));
        RequireNotificationPipelineExecuted(typeof(CommandCompletedNotification));

        // The handler published inside the command's transaction, so the notification went to the outbox and the
        // processor delivers it in a scope of its own; an in-process publish would have run in this scenario's scope.
        var delivery = await diagnostics.WaitForUserCreatedAsync(userId, DeliveryTimeout, cancellationToken);
        Require(delivery.Notification == new UserCreatedNotification(userId, "Ada"),
            $"The outbox delivered {delivery.Notification}, not what the handler published.");
        Require(delivery.HandlerScopeId != scenario.ScopeId,
            "UserCreatedNotification was handled in the publishing request's scope: it did not go through the outbox.");

        // The handler signals before the notification pipeline around it has finished.
        await diagnostics.WaitForNotificationPipelineAsync(typeof(UserCreatedNotification), DeliveryTimeout, cancellationToken);
        RequireNotificationPipelineExecuted(typeof(UserCreatedNotification));

        var user = await scenario.Cqrs.Send(new GetUserQuery(userId), cancellationToken);
        Require(user == new User("Ada", userId), $"GetUserQuery returned {user?.ToString() ?? "null"}.");
        RequireNotificationPipelineExecuted(typeof(QueryInitiatedNotification<User?>));
        RequireNotificationPipelineExecuted(typeof(QueryCompletedNotification<User?>));
    }

    private async Task RunOutboxSerializationTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var shipTo = new ShippingAddress { Street = "1 Main Street", City = "Springfield", PostalCode = "12345" };
        OrderLine[] lines =
        [
            new() { Sku = "SKU-1", Quantity = 2, UnitPrice = 9.99m },
            new() { Sku = "SKU-2", Quantity = 1, UnitPrice = 0.05m }
        ];
        string[] tags = ["gift", "express"];

        var clock = scenario.Services.GetRequiredService<TimeProvider>();
        var before = clock.GetUtcNow();
        var result = await scenario.Cqrs.Send(new PlaceOrderCommand { ShipTo = shipTo, Lines = lines, Tags = tags }, cancellationToken);
        var after = clock.GetUtcNow();

        Require(result.IsSuccess, "PlaceOrderCommand did not succeed.");
        var delivered = await diagnostics.WaitForOrderPlacedAsync(result.Value, DeliveryTimeout, cancellationToken);

        // The generated serializer wrote the notification and read it back into new objects with the same values.
        Require(!ReferenceEquals(delivered.ShipTo, shipTo), "OrderPlacedNotification was delivered without being serialized.");
        Require(delivered.ShipTo == shipTo, $"The delivered address is {delivered.ShipTo}.");
        Require(delivered.Lines.SequenceEqual(lines), $"The delivered lines are [{string.Join(", ", delivered.Lines)}].");
        Require(delivered.Tags.SequenceEqual(tags), $"The delivered tags are [{string.Join(", ", delivered.Tags)}].");
        Require(delivered.Status == OrderStatus.Paid, $"The delivered status is {delivered.Status}.");
        Require(delivered.PlacedAt >= before && delivered.PlacedAt <= after,
            $"The delivered PlacedAt {delivered.PlacedAt:O} is not the time the order was placed.");
    }

    // The same application with RunMode.Queued: every command and query goes through the background queue and runs in a
    // scope of its own, and its result (a value type among them) comes back to the caller. Its context still names the
    // caller, captured from the caller's scope before the hand-off, so the rate limiter keys PingCommand on the caller.
    // A stream is not queued: it runs on the enumerating flow, in the caller's scope. The application's startup
    // validation runs as the host starts, streams and all.
    private static async Task RunQueuedDispatchTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.ConfigureContainer(new DefaultServiceProviderFactory(SampleApplication.ValidatingProviderOptions));
        builder.Services.AddSampleApplication();
        builder.Services.AddCqrsGenerated(b => b.ConfigureDispatcher(o => o.RunMode = RunMode.Queued));

        using var host = builder.Build();
        await host.StartAsync(cancellationToken);
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentUser>().UserId = scenario.UserId;
            var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            var callerScopeId = scope.ServiceProvider.GetRequiredService<SampleScopedMarker>().Id;

            var handlerScopeId = await cqrs.Send(new ScopeProbeQuery(), cancellationToken);
            Require(handlerScopeId != callerScopeId, "A queued dispatch ran in its caller's scope, not in one of its own.");

            var ping = new PingCommand();
            Require((await cqrs.Send(ping, cancellationToken)).IsSuccess, "A queued PingCommand did not succeed.");
            Require(ping.Context?.UserId == scenario.UserId,
                $"A queued PingCommand's context names user '{ping.Context?.UserId}', not its caller '{scenario.UserId}'.");

            var sum = await cqrs.Send(new AddNumbersQuery(20, 22), cancellationToken);
            Require(sum == 42, $"A queued AddNumbersQuery returned {sum}.");

            var token = await cqrs.Send(new MintTokenCommand { Subject = "queued" }, cancellationToken);
            Require(token is { IsSuccess: true, Value: "token:queued" }, $"A queued MintTokenCommand returned {token}.");

            var items = new List<StreamProbeItem>();
            await foreach (var item in cqrs.Stream(new StreamProbeRequest(2), cancellationToken))
                items.Add(item);
            Require(items.Select(x => x.Value).SequenceEqual([1, 2]),
                $"A stream under queued dispatch streamed [{string.Join(", ", items.Select(x => x.Value))}].");
            Require(items.All(x => x.ScopeId == callerScopeId), "A stream under queued dispatch ran outside its caller's scope.");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    // The same application on the Redis stores, built but never started: building it validates the container and the
    // startup validator runs the Redis option checks, yet nothing resolves a store, so nothing connects. The Native AOT
    // binary therefore runs the Redis registration code, not only compiles it.
    private static Task RunRedisRegistrationTestAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        const string connectionString = "127.0.0.1:6379,abortConnect=false";

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.ConfigureContainer(new DefaultServiceProviderFactory(SampleApplication.ValidatingProviderOptions));
        builder.Services.AddSampleApplication();
        builder.Services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.Transactional().UseRedis(connectionString))
            .UseIdempotency(i => i.UseRedis(connectionString)));

        RequireServedByRedis<IOutboxStore, RedisOutboxOptions>(builder.Services);
        RequireServedByRedis<IInboxStore, RedisOutboxOptions>(builder.Services);
        RequireServedByRedis<IIdempotencyStore, RedisIdempotencyOptions>(builder.Services);

        using var host = builder.Build();
        host.Services.GetRequiredService<IStartupValidator>().Validate();
        return Task.CompletedTask;

        // A Redis verb replaces the store with a singleton built by a factory over the connection its registration was
        // given, which CQRSharp.Redis keeps in a singleton of its own per feature (named by the feature's options type).
        // Reading the registrations rather than resolving the store is what keeps the scenario from connecting.
        static void RequireServedByRedis<TService, TFeatureOptions>(IServiceCollection services)
        {
            Require(services.Where(d => d.ServiceType == typeof(TService)).ToList()
                    is [{ Lifetime: ServiceLifetime.Singleton, ImplementationFactory: not null }],
                $"{typeof(TService).Name} is not registered once, as the singleton a Redis verb registers: the in-memory store was not replaced.");

            var redis = typeof(TFeatureOptions).Assembly;
            Require(services.Any(d => d.ServiceType.Assembly == redis && d.ServiceType.GenericTypeArguments is [var feature] && feature == typeof(TFeatureOptions)),
                $"No CQRSharp.Redis connection is registered for {typeof(TFeatureOptions).Name}, so {typeof(TService).Name} is not on Redis.");
        }
    }
}

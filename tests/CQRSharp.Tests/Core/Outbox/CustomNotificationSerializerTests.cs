using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A serializer registered with <c>AddNotificationSerializer&lt;T&gt;()</c> replaces the generated one outright and is
///     then the only authority on durability: what it names is stored in the outbox under its name and delivered through
///     it, and what it does not name — a <see cref="NotificationNameAttribute" /> notification included — is dispatched
///     in-process. Runs the real dispatcher, in-memory store and processor on a <see cref="FakeTimeProvider" /> that never
///     advances, so the processor only wakes when a stored message signals it.
/// </summary>
public sealed class CustomNotificationSerializerTests
{
    private static ServiceProvider Build(bool registerSerializerFirst = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<CustomSerializerProbe>();
        if (registerSerializerFirst) services.AddNotificationSerializer<TextNotificationSerializer>();
        services.AddCqrsGenerated(b => b.UseOutbox(o => o
            .Enabled()
            .UseInMemoryStore()
            .ConfigureProcessor(p => p.PollingInterval = TimeSpan.FromHours(1))));
        if (!registerSerializerFirst) services.AddNotificationSerializer<TextNotificationSerializer>();
        return services.BuildServiceProvider();
    }

    private static async Task PublishAsync(IServiceProvider provider, INotification notification)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(notification, TestContext.Current.CancellationToken);
    }

    private static IReadOnlyCollection<OutboxMessage> Stored(IServiceProvider provider)
        => ((InMemoryOutboxStore)provider.GetRequiredService<IOutboxStore>()).Snapshot();

    [Fact(DisplayName = "A notification only the custom serializer names is stored in the outbox under the name it gives")]
    public async Task A_notification_only_the_custom_serializer_names_is_stored_under_its_name()
    {
        await using var provider = Build();

        await PublishAsync(provider, new CustomSerializedNotification("hello"));

        var message = Stored(provider).Should().ContainSingle().Subject;
        message.NotificationType.Should().Be(TextNotificationSerializer.Name);
        message.HandlerName.Should().Be(typeof(CustomSerializedNotificationHandler).FullName);
        message.Payload.Should().Equal(Encoding.UTF8.GetBytes("hello"), "the custom serializer wrote the payload");
        provider.GetRequiredService<CustomSerializerProbe>().Delivered.Task.IsCompleted.Should().BeFalse("it went to the outbox, not in-process");
    }

    [Fact(DisplayName = "The processor delivers what the custom serializer stored, read back through the same serializer")]
    public async Task The_processor_delivers_through_the_custom_serializer()
    {
        await using var provider = Build();
        var processor = provider.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        await processor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await PublishAsync(provider, new CustomSerializedNotification("across the outbox"));

            // The clock never moves: only the stored message's wake-up signal can make the processor poll.
            var delivered = await provider.GetRequiredService<CustomSerializerProbe>().Delivered.Task
                .WaitAsync(TestContext.Current.CancellationToken);

            delivered.Text.Should().Be("across the outbox");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "A [NotificationName] notification the custom serializer does not name is not durable: it is dispatched in-process")]
    public async Task A_notification_the_custom_serializer_does_not_name_is_dispatched_in_process()
    {
        await using var provider = Build();
        var notification = new GeneratedOnlyNotification(Guid.NewGuid());

        await PublishAsync(provider, notification);

        Stored(provider).Should().BeEmpty("the generated serializer that would have named it has been replaced");
        provider.GetRequiredService<CustomSerializerProbe>().InProcess.Should().ContainSingle().Which.Should().Be(notification);
    }

    [Fact(DisplayName = "Storing a notification the serializer does not name fails loudly instead of inventing a name")]
    public async Task Storing_a_notification_the_serializer_does_not_name_throws()
    {
        await using var provider = Build();

        var act = () => OutboxMessageFactory.Create(
            new INotification[] { new GeneratedOnlyNotification(Guid.NewGuid()) },
            provider.GetRequiredService<INotificationSerializer>(),
            provider.GetRequiredService<INotificationSubscriptionRegistry>(),
            provider.GetRequiredService<TimeProvider>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{typeof(GeneratedOnlyNotification).FullName}'*no name*AddNotificationSerializer*");
    }

    [Theory(DisplayName = "AddNotificationSerializer replaces the generated serializer, whichever of the two registrations runs first")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddNotificationSerializer_replaces_the_generated_serializer(bool registerSerializerFirst)
    {
        await using var provider = Build(registerSerializerFirst);

        provider.GetServices<INotificationSerializer>().Should().ContainSingle().Which.Should().BeOfType<TextNotificationSerializer>();
        provider.GetRequiredService<INotificationSerializer>().TryGetNotificationName(typeof(TestNotification), out _)
            .Should().BeFalse("the generated names are not consulted any more, [NotificationName] or not");
    }

    [Fact(DisplayName = "CQRCONF003 follows the custom serializer: silent for what it names, reported for what it does not")]
    public async Task CQRCONF003_follows_the_custom_serializer()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var conf003 = scope.ServiceProvider.GetRequiredService<ICqrsDiagnostics>().DescribeConfiguration()
            .Where(i => i.Code == "CQRCONF003")
            .ToArray();

        conf003.Should().NotContain(i => i.Message.Contains(typeof(CustomSerializedNotification).FullName + "'"));
        conf003.Should().ContainSingle(i => i.Message.Contains(typeof(GeneratedOnlyNotification).FullName + "'"))
            .Which.Message.Should().Contain(typeof(TextNotificationSerializer).FullName!);
    }
}

/// <summary>A handled notification without <see cref="NotificationNameAttribute" />: only the custom serializer names it.</summary>
public sealed record CustomSerializedNotification(string Text) : INotification;

/// <summary>A notification the generated serializer would name, but the custom serializer does not.</summary>
[NotificationName("tests.custom-serializer.generated-only")]
public sealed record GeneratedOnlyNotification(Guid Id) : INotification;

public sealed class CustomSerializerProbe
{
    public TaskCompletionSource<CustomSerializedNotification> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<GeneratedOnlyNotification> InProcess { get; } = new();
}

internal sealed class CustomSerializedNotificationHandler(CustomSerializerProbe probe) : INotificationHandler<CustomSerializedNotification>
{
    public Task Handle(CustomSerializedNotification notification, CancellationToken cancellationToken)
    {
        probe.Delivered.TrySetResult(notification);
        return Task.CompletedTask;
    }
}

internal sealed class GeneratedOnlyNotificationHandler(CustomSerializerProbe probe) : INotificationHandler<GeneratedOnlyNotification>
{
    public Task Handle(GeneratedOnlyNotification notification, CancellationToken cancellationToken)
    {
        probe.InProcess.Enqueue(notification);
        return Task.CompletedTask;
    }
}

/// <summary>A hand-written serializer that names <see cref="CustomSerializedNotification" /> alone; its payload is the text.</summary>
public sealed class TextNotificationSerializer : INotificationSerializer
{
    public const string Name = "tests.custom-serializer.text";

    public bool TryGetNotificationName(Type notificationType, [NotNullWhen(true)] out string? notificationName)
    {
        notificationName = notificationType == typeof(CustomSerializedNotification) ? Name : null;
        return notificationName is not null;
    }

    public byte[] Serialize(INotification notification)
        => notification is CustomSerializedNotification custom
            ? Encoding.UTF8.GetBytes(custom.Text)
            : throw new InvalidOperationException($"'{notification.GetType().FullName}' is not a notification this serializer names.");

    public INotification? Deserialize(string notificationName, byte[] payload)
        => notificationName == Name ? new CustomSerializedNotification(Encoding.UTF8.GetString(payload)) : null;
}

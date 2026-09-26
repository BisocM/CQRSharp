using System.Diagnostics.CodeAnalysis;
using System.Text;
using CQRSharp.Transports;
using Xunit;

namespace CQRSharp.Testing;

/// <summary>
///     The reusable conformance suite for every <see cref="INotificationTransport" />: what the outbox processor relies on
///     when it hands a transport a stored notification. A send reported as <see cref="TransportSendResult.Sent" /> is at the
///     destination with everything the receiver needs (the message id, the name, the payload byte for byte, the partition
///     key, the trace context); sends made one after another arrive in that order; a repeated send carries the same message
///     id, so the receiver can recognise it; a destination that cannot be reached is <see cref="TransportSendStatus.Unavailable" />,
///     never a rejection; and a cancelled send throws rather than report an outcome. Derive from it, create the transport
///     under test, and receive what it delivered.
/// </summary>
/// <remarks>
///     Every test works on a notification name of its own (<c>contract.&lt;guid&gt;</c>), so the suite can share one
///     destination between tests and test processes: route that name, and receive only what was sent under it. The suite
///     waits for what it expects to arrive, never for a fixed time: a message that never arrives is left to the test
///     runner's cancellation or hang detection.
/// </remarks>
[Experimental("CQREXP001", UrlFormat = "https://github.com/BisocM/CQRSharp/blob/Release/docs/diagnostics.md#experimental-apis-cqrexp")]
public abstract class NotificationTransportContractTests : IAsyncLifetime
{
    /// <summary>Runs before each test; nothing by default.</summary>
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Runs after each test. Override it to dispose what <see cref="CreateTransportAsync" /> opened.</summary>
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     Creates the transport under test, configured to forward notifications stored under <paramref name="notificationName" />,
    ///     and whatever receives them at the destination, so that <see cref="ReceiveAsync" /> sees every message it sends
    ///     under that name from now on.
    /// </summary>
    /// <param name="notificationName">The name to route, unique to the calling test.</param>
    protected abstract Task<INotificationTransport> CreateTransportAsync(string notificationName);

    /// <summary>
    ///     The next message that arrives at the destination under the name <see cref="CreateTransportAsync" /> was given, read
    ///     back as the receiver sees it, waiting until one does.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the test is.</param>
    protected abstract Task<ReceivedTransportMessage> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Makes the destination unreachable for the transport (stops the broker, cuts the connection), and returns whether it
    ///     could; the availability test is skipped when it returns <see langword="false" />, which it does by default.
    /// </summary>
    protected virtual Task<bool> MakeUnavailableAsync() => Task.FromResult(false);

    /// <summary>
    ///     Makes the destination reachable again after <see cref="MakeUnavailableAsync" />, and completes once the transport
    ///     can reach it (its connection is open again), so the next send is expected to succeed.
    /// </summary>
    protected virtual Task RestoreAvailabilityAsync() => Task.CompletedTask;

    private static string NewName() => $"contract.{Guid.NewGuid():N}";

    private static OutboundNotification Outbound(string name, int seq, string? partitionKey = null, int attempt = 0, Guid? messageId = null)
        => new(
            messageId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            name,
            Encoding.UTF8.GetBytes($"{{\"seq\":{seq}}}"),
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc).AddSeconds(seq),
            partitionKey,
            "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            null,
            attempt);

    private Task<ReceivedTransportMessage> ReceiveOneAsync() => ReceiveAsync(TestContext.Current.CancellationToken);

    /// <summary>Contract: the transport has a name an outbox message can be addressed to.</summary>
    [Fact]
    public async Task The_transport_has_a_valid_name()
    {
        var transport = await CreateTransportAsync(NewName());

        Assert.False(string.IsNullOrWhiteSpace(transport.Name), "The transport's name addresses its outbox messages; it must not be empty.");
        Assert.True(transport.Name.Length <= 256, $"The transport's name must fit an outbox handler name (256 characters); it has {transport.Name.Length}.");
        Assert.NotNull(transport.Declaration);
    }

    /// <summary>Contract: the transport routes the name it was configured for, and not another.</summary>
    [Fact]
    public async Task The_transport_routes_what_it_was_configured_for()
    {
        var name = NewName();
        var transport = await CreateTransportAsync(name);

        Assert.True(transport.Routes(name, typeof(ContractNotification)), $"The transport was configured to forward '{name}' but does not route it.");
        Assert.False(transport.Routes(NewName(), typeof(ContractNotification)), "The transport routes a name it was never configured for.");
    }

    /// <summary>
    ///     Contract: a send reported as sent is at the destination, with its message id, name, payload (byte for byte),
    ///     partition key and trace context.
    /// </summary>
    [Fact]
    public async Task A_sent_notification_arrives_as_it_was_sent()
    {
        var name = NewName();
        var transport = await CreateTransportAsync(name);
        var sent = Outbound(name, 1, partitionKey: "order-17");

        var result = await transport.SendAsync(sent, CancellationToken.None);

        ContractAssert.Equal(TransportSendStatus.Sent, result.Status, $"The send's status (reason: {result.Reason})");
        var received = await ReceiveOneAsync();
        ContractAssert.Equal(sent.MessageId, received.MessageId, "The received message id");
        ContractAssert.Equal(sent.NotificationName, received.NotificationName, "The received notification name");
        ContractAssert.SequenceEqual(sent.Payload, received.Payload, "The received payload");
        ContractAssert.Equal(sent.PartitionKey, received.PartitionKey, "The received partition key");
        ContractAssert.Equal(sent.TraceParent, received.TraceParent, "The received traceparent");
    }

    /// <summary>Contract: notifications sent one after another arrive in the order they were sent.</summary>
    [Fact]
    public async Task Sequential_sends_arrive_in_order()
    {
        var name = NewName();
        var transport = await CreateTransportAsync(name);
        var sent = Enumerable.Range(1, 5).Select(seq => Outbound(name, seq, partitionKey: "order-18")).ToList();

        foreach (var message in sent)
            ContractAssert.Equal(TransportSendStatus.Sent, (await transport.SendAsync(message, CancellationToken.None)).Status, "Each send's status");

        var received = new List<Guid?>();
        for (var i = 0; i < sent.Count; i++)
            received.Add((await ReceiveOneAsync()).MessageId);

        ContractAssert.SequenceEqual(sent.Select(m => (Guid?)m.MessageId).ToList(), received, "The order the messages arrived in");
    }

    /// <summary>
    ///     Contract: a message sent again (a retry, or a send whose outcome was lost) arrives with the same message id, so the
    ///     receiver can recognise the repetition.
    /// </summary>
    [Fact]
    public async Task A_repeated_send_carries_the_same_message_id()
    {
        var name = NewName();
        var transport = await CreateTransportAsync(name);
        var messageId = Guid.NewGuid();

        await transport.SendAsync(Outbound(name, 1, attempt: 0, messageId: messageId), CancellationToken.None);
        await transport.SendAsync(Outbound(name, 1, attempt: 1, messageId: messageId), CancellationToken.None);

        ContractAssert.Equal(messageId, (await ReceiveOneAsync()).MessageId, "The first arrival's message id");
        ContractAssert.Equal(messageId, (await ReceiveOneAsync()).MessageId, "The repeated arrival's message id");
    }

    /// <summary>
    ///     Contract: a send cancelled by its token (the host stopping) throws <see cref="OperationCanceledException" />
    ///     instead of reporting an outcome, so the processor hands the message back rather than charge or settle it.
    /// </summary>
    [Fact]
    public async Task A_cancelled_send_throws()
    {
        var name = NewName();
        var transport = await CreateTransportAsync(name);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(Outbound(name, 1), cancelled.Token));
    }

    /// <summary>
    ///     Contract: while the destination cannot be reached, a send is <see cref="TransportSendStatus.Unavailable" />, never
    ///     <see cref="TransportSendStatus.Rejected" /> (which would charge an attempt and, in an outage, dead-letter it), and
    ///     sends succeed again once it is back.
    /// </summary>
    [Fact]
    public async Task An_unreachable_destination_is_unavailable_never_rejected()
    {
        var name = NewName();
        var transport = await CreateTransportAsync(name);
        Assert.SkipUnless(await MakeUnavailableAsync(), "This suite's implementation cannot make the destination unreachable.");

        TransportSendResult result;
        try
        {
            result = await transport.SendAsync(Outbound(name, 1), CancellationToken.None);
        }
        finally
        {
            await RestoreAvailabilityAsync();
        }

        ContractAssert.Equal(TransportSendStatus.Unavailable, result.Status, $"The status of a send to an unreachable destination (reason: {result.Reason})");
        Assert.False(string.IsNullOrWhiteSpace(result.Reason), "An unavailable send must say why.");

        var again = Outbound(name, 2);
        var retried = await transport.SendAsync(again, CancellationToken.None);
        ContractAssert.Equal(TransportSendStatus.Sent, retried.Status, $"The status of a send once the destination is back (reason: {retried.Reason})");
        ContractAssert.Equal(again.MessageId, (await ReceiveOneAsync()).MessageId, "The message id that arrived");
    }

    // A notification type for the routing question: the suite routes by name, which is what an outbox message carries.
    private sealed record ContractNotification : INotification;
}

/// <summary>A message that arrived at a transport's destination, as the receiver reads it back.</summary>
/// <param name="MessageId">The message id the receiver sees, as a <see cref="Guid" />; <see langword="null" /> when the message carried none.</param>
/// <param name="NotificationName">The notification name the message carried.</param>
/// <param name="Payload">The payload the message carried.</param>
/// <param name="PartitionKey">The partition key the message carried, or <see langword="null" />.</param>
/// <param name="TraceParent">The W3C <c>traceparent</c> the message carried, or <see langword="null" />.</param>
[Experimental("CQREXP001", UrlFormat = "https://github.com/BisocM/CQRSharp/blob/Release/docs/diagnostics.md#experimental-apis-cqrexp")]
public sealed record ReceivedTransportMessage(Guid? MessageId, string NotificationName, byte[] Payload, string? PartitionKey, string? TraceParent);

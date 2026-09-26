namespace CQRSharp.Persistence;

/// <summary>
///     An <see cref="IOutboxStore" /> that can hold a message until a time in the future, so notifications can be
///     published for later delivery (<c>ICqrsDispatcher.PublishAt</c> / <c>PublishAfter</c>). The interface adds no
///     member: implementing it is the store's declaration that it keeps the promises below, which the dispatcher relies on
///     and checks for before it accepts a scheduled publish. A store that does not implement it keeps working for every
///     other publish; a scheduled publish into it fails, before anything is buffered or stored.
/// </summary>
/// <remarks>
///     <para>
///         A scheduled message is stored like any other, through <see cref="IOutboxStore.StoreAsync" />, in the same call
///         (and, for a store that joins the unit of work, the same transaction) as the messages published beside it. It is
///         built with its <see cref="OutboxMessage.CreatedAt" /> <em>and</em> its <see cref="OutboxMessage.NextRetryAt" />
///         both at its due time, which lies in the future: the due time is where it takes its place in the delivery order,
///         as if it had been published then, and it is not claimable before it. A scheduling store:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 stores <see cref="OutboxMessage.CreatedAt" /> and <see cref="OutboxMessage.NextRetryAt" /> exactly as
///                 given, even when they lie in the future, and never claims a pending message before its
///                 <see cref="OutboxMessage.NextRetryAt" /> (which every store already does for a back-off);
///             </description>
///         </item>
///         <item>
///             <description>
///                 orders it by that <see cref="OutboxMessage.CreatedAt" /> like any other message, so in its partition it
///                 holds back only the messages created after its due time, and never the ones created before it;
///             </description>
///         </item>
///         <item>
///             <description>
///                 leaves a message whose <see cref="OutboxMessage.CreatedAt" /> is still in the future out of the backlog's
///                 <see cref="OutboxBacklog.PendingCount" /> and <see cref="OutboxBacklog.OldestPendingCreatedAt" />, and
///                 counts it in <see cref="OutboxBacklog.ScheduledCount" /> instead: it is not late, so it is neither
///                 backlog nor lag.
///             </description>
///         </item>
///     </list>
///     <para>
///         Once due, a scheduled message is an ordinary one: it is claimed, retried with back-off, deferred,
///         dead-lettered and requeued exactly as the contract describes, with its due time as its creation time. The
///         outbox store contract suite (<c>OutboxStoreContractTests</c> in <c>CQRSharp.Testing.Xunit.V3</c>) checks these
///         promises for a store that implements this interface.
///     </para>
/// </remarks>
public interface ISchedulingOutboxStore : IOutboxStore;

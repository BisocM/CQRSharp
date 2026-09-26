using CQRSharp.Persistence;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     The <see cref="IInboxStore" /> that pairs with <see cref="RedisOutboxStore" />: one key per completed delivery
///     (<c>{KeyPrefix}inbox:{messageId}:{handlerName}</c>), created with <c>SET NX</c> so two recorders of one delivery
///     race on a single server-side operation, and expired by Redis after <see cref="RedisOutboxOptions.InboxRetention" />.
///     Not transactional with a handler's database, so the record is written right after the handler's unit of work
///     commits: a failed commit never marks a delivery done, but a duplicate remains possible — a crash or a failed
///     record between the commit and the record, or a lease that expires while a slow handler still runs (see
///     <see cref="IInboxStore" />).
/// </summary>
internal sealed class RedisInboxStore : IInboxStore
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _keyPrefix;
    private readonly int _database;
    private readonly TimeSpan _retention;

    public RedisInboxStore(IConnectionMultiplexer mux, IOptions<RedisOutboxOptions> options)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        var opts = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _keyPrefix = opts.KeyPrefix;
        _database = opts.Database;
        _retention = opts.InboxRetention;
    }

    // Redis never takes part in a database transaction: a delivery is recorded after the unit of work commits.
    public bool JoinsUnitOfWork => false;

    public Task<bool> IsDeliveredAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerName);
        return _mux.GetDatabase(_database).KeyExistsAsync(Key(messageId, handlerName));
    }

    public Task<bool> RecordDeliveryAsync(Guid messageId, string handlerName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerName);
        return _mux.GetDatabase(_database).StringSetAsync(Key(messageId, handlerName), 1, _retention, When.NotExists);
    }

    private RedisKey Key(Guid messageId, string handlerName) => $"{_keyPrefix}inbox:{messageId:N}:{handlerName}";
}

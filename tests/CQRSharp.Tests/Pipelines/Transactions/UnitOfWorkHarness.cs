using CQRSharp.Core.Outbox;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     A real container (outbox enabled, a recording unit of work, a recording store and signal that share one call log)
///     and one of its scopes, for driving the unit-of-work behaviors directly: the production scoped outbox, subscription
///     registry and serializer, with the transaction's participants observable.
/// </summary>
internal sealed class UnitOfWorkHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private UnitOfWorkHarness(ServiceProvider provider, TransactionLog log, RecordingUnitOfWork unitOfWork, RecordingOutboxStore store)
    {
        _provider = provider;
        _scope = provider.CreateAsyncScope();
        Log = log;
        UnitOfWork = unitOfWork;
        Store = store;
    }

    public TransactionLog Log { get; }
    public RecordingUnitOfWork UnitOfWork { get; }
    public RecordingOutboxStore Store { get; }
    public IServiceProvider Services => _scope.ServiceProvider;
    public ScopedOutbox Outbox => Services.GetRequiredService<ScopedOutbox>();

    public static UnitOfWorkHarness Create(bool storeJoinsUnitOfWork = true, Action<UnitOfWorkOptions>? configure = null)
    {
        var log = new TransactionLog();
        var unitOfWork = new RecordingUnitOfWork(log);
        var store = new RecordingOutboxStore(storeJoinsUnitOfWork, log);

        var services = new ServiceCollection();
        services.AddSingleton<IOutboxSignal>(new RecordingOutboxSignal(log));
        services.AddCqrsGenerated(b => b
            .UseOutbox(o => o.Enabled().UseStore(s => s.AddSingleton<IOutboxStore>(store)))
            .UseUnitOfWork(_ => unitOfWork, configure));
        return new UnitOfWorkHarness(services.BuildServiceProvider(), log, unitOfWork, store);
    }

    public UnitOfWorkBehavior<TRequest, TResult> Behavior<TRequest, TResult>(ILogger<UnitOfWorkBehavior<TRequest, TResult>>? logger = null)
        where TRequest : IRequest
        => new(
            logger ?? NullLogger<UnitOfWorkBehavior<TRequest, TResult>>.Instance,
            UnitOfWork,
            Services.GetRequiredService<IOptions<UnitOfWorkOptions>>(),
            Services);

    public StreamUnitOfWorkBehavior<TRequest, TItem> StreamBehavior<TRequest, TItem>(ILogger<StreamUnitOfWorkBehavior<TRequest, TItem>>? logger = null)
        where TRequest : IRequest
        => new(
            logger ?? NullLogger<StreamUnitOfWorkBehavior<TRequest, TItem>>.Instance,
            UnitOfWork,
            Services.GetRequiredService<IOptions<UnitOfWorkOptions>>(),
            Services);

    /// <summary>
    ///     Runs <paramref name="body" /> as a request of the scope, the way the executor does: registered with the scope's
    ///     outbox, current for everything the body awaits, and settled when it ends (what it buffered and no unit of work
    ///     took is stored on success, discarded on a failure or a failed result).
    /// </summary>
    public async Task<T> AsRequest<T>(Func<Task<T>> body)
    {
        var request = RequestOutboxScope.Begin(Services) ?? throw new InvalidOperationException("The outbox is not registered.");
        T result;
        try
        {
            result = await body();
        }
        catch
        {
            request.Abandon();
            throw;
        }

        if (result is CommandResult { IsSuccess: false }) request.Abandon();
        else await request.CompleteAsync();
        return result;
    }

    /// <summary>Publishes a durable notification from inside the running request.</summary>
    public void Publish() => Outbox.TryBuffer(new TestNotification()).Should().BeTrue("a publish inside a running request is buffered");

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}

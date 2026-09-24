using CQRSharp.Core.BackgroundTasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Tests.Core;

/// <summary>
///     RunMode.Queued from inside the queue's own work: a request sent from any work item of the queue runs at once in a
///     scope of its own, whatever enqueued the item, so a single consumer slot is never held waiting for itself.
/// </summary>
public sealed class QueuedExecutionTests
{
    [Fact(DisplayName = "Queued mode: work enqueued through IBackgroundTaskManager that sends a request completes, even with a single consumer")]
    public async Task Work_enqueued_directly_can_send_a_queued_request()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddCqrsGenerated(b => b
                .ConfigureDispatcher(o => o.RunMode = RunMode.Queued)
                .ConfigureQueue(o => o.ConsumerCount = 1)))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var work = host.Services.GetRequiredService<IBackgroundTaskManager>().EnqueueAsync(async workerToken =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new EnqueuedWorkCommand(), workerToken);
            }, TestContext.Current.CancellationToken);

            // A deadlock guard, not a timing assertion: the work finishes at once unless the request queues behind it.
            (await work.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

public sealed class EnqueuedWorkCommand : CommandBase;

public sealed class EnqueuedWorkCommandHandler : ICommandHandler<EnqueuedWorkCommand>
{
    public Task<CommandResult> Handle(EnqueuedWorkCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}

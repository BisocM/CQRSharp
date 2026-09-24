using CQRSharp.Core.Outbox;
using CQRSharp.Tests.Shared;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The scope's outbox buffer (<see cref="ScopedOutbox" />): it buffers only inside a running request of its scope,
///     and each request settles exactly what it and its finished nested requests buffered.
/// </summary>
public sealed class ScopedOutboxTests
{
    [Fact(DisplayName = "Outside any running request of the scope nothing is buffered")]
    public void Nothing_is_buffered_outside_a_request()
    {
        var outbox = new ScopedOutbox();

        outbox.TryBuffer(new TestNotification()).Should().BeFalse();
        outbox.Count.Should().Be(0);
    }

    [Fact(DisplayName = "A request that completes gets back what it buffered, in order, and the buffer is empty")]
    public async Task Completed_request_takes_its_notifications_in_order()
    {
        var outbox = new ScopedOutbox();
        var a = new Ping();
        var b = new Pong();

        var settled = await Run(() =>
        {
            var owner = outbox.BeginRequest();
            outbox.TryBuffer(a).Should().BeTrue();
            outbox.TryBuffer(b).Should().BeTrue();
            return outbox.CompleteRequest(owner);
        });

        settled.Should().Equal(a, b);
        outbox.Count.Should().Be(0);
    }

    [Fact(DisplayName = "A request that is abandoned discards what it buffered")]
    public async Task Abandoned_request_discards_its_notifications()
    {
        var outbox = new ScopedOutbox();

        await Run(() =>
        {
            var owner = outbox.BeginRequest();
            outbox.TryBuffer(new Ping());
            outbox.AbandonRequest(owner);
            return 0;
        });

        outbox.Count.Should().Be(0);
    }

    [Fact(DisplayName = "A nested request that completes leaves its notifications for its caller, which settles them with its own")]
    public async Task Nested_request_is_settled_by_its_caller()
    {
        var outbox = new ScopedOutbox();
        var outer = new Ping();
        var inner = new Pong();

        var settled = await Run(async () =>
        {
            var owner = outbox.BeginRequest();
            outbox.TryBuffer(outer);

            var nestedSettled = await Run(() =>
            {
                var nested = outbox.BeginRequest();
                outbox.TryBuffer(inner);
                return outbox.CompleteRequest(nested);
            });
            nestedSettled.Should().BeEmpty("a nested request's caller settles it");

            return outbox.CompleteRequest(owner);
        });

        settled.Should().Equal(outer, inner);
    }

    [Fact(DisplayName = "Two requests running side by side in one scope settle independently: a failure never takes a sibling's notifications")]
    public async Task Sibling_requests_settle_independently()
    {
        var outbox = new ScopedOutbox();
        var succeeded = new Ping();
        var failed = new Pong();
        var firstBuffered = new TaskCompletionSource();
        var secondDone = new TaskCompletionSource();

        var first = Run(async () =>
        {
            var owner = outbox.BeginRequest();
            outbox.TryBuffer(succeeded);
            firstBuffered.SetResult();
            await secondDone.Task;
            return outbox.CompleteRequest(owner);
        });

        await firstBuffered.Task;
        await Run(() =>
        {
            var owner = outbox.BeginRequest();
            outbox.TryBuffer(failed);
            outbox.AbandonRequest(owner);
            return 0;
        });
        secondDone.SetResult();

        (await first).Should().Equal(succeeded);
        outbox.Count.Should().Be(0);
    }

    [Fact(DisplayName = "A nested request still running when its caller completes keeps its notifications and settles them itself")]
    public async Task Running_nested_request_settles_itself()
    {
        var outbox = new ScopedOutbox();
        var outer = new Ping();
        var inner = new Pong();
        var nestedBuffered = new TaskCompletionSource();
        var callerDone = new TaskCompletionSource();
        Task<IReadOnlyList<INotification>>? nestedTask = null;

        var callerSettled = await Run(async () =>
        {
            var owner = outbox.BeginRequest();
            outbox.TryBuffer(outer);

            // Fired and forgotten: the caller does not wait for it.
            nestedTask = Run(async () =>
            {
                var nested = outbox.BeginRequest();
                outbox.TryBuffer(inner);
                nestedBuffered.SetResult();
                await callerDone.Task;
                return outbox.CompleteRequest(nested);
            });

            await nestedBuffered.Task;
            return outbox.CompleteRequest(owner);
        });
        callerDone.SetResult();

        callerSettled.Should().Equal(outer);
        (await nestedTask!).Should().Equal(inner);
    }

    [Fact(DisplayName = "A publish while another request of the scope runs, but from outside it, is not buffered")]
    public async Task Publish_outside_the_running_request_is_not_buffered()
    {
        var outbox = new ScopedOutbox();
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var request = Run(async () =>
        {
            var owner = outbox.BeginRequest();
            running.SetResult();
            await release.Task;
            return outbox.CompleteRequest(owner);
        });

        await running.Task;
        outbox.TryBuffer(new Ping()).Should().BeFalse("nothing in this flow belongs to the running request");
        release.SetResult();
        (await request).Should().BeEmpty();
    }

    [Fact(DisplayName = "Buffering throws on a null notification")]
    public void Buffering_null_throws()
    {
        var act = () => new ScopedOutbox().TryBuffer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact(DisplayName = "The buffer loses nothing under concurrent publishes (parallel publish strategies)")]
    public async Task Buffer_is_safe_under_concurrent_publishes()
    {
        var outbox = new ScopedOutbox();

        var settled = await Run(async () =>
        {
            var owner = outbox.BeginRequest();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 2000; i++) outbox.TryBuffer(new TestNotification());
            })));
            return outbox.CompleteRequest(owner);
        });

        settled.Should().HaveCount(16000);
    }

    // Each request runs in an async flow of its own, as the executor runs it: the owner it begins stays in that flow.
    private static async Task<T> Run<T>(Func<T> body)
    {
        await Task.Yield();
        return body();
    }

    private static async Task<T> Run<T>(Func<Task<T>> body)
    {
        await Task.Yield();
        return await body();
    }

    private sealed record Ping : INotification;

    private sealed record Pong : INotification;
}

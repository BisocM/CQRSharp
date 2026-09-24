using CQRSharp.Testing;
using FluentAssertions;

namespace CQRSharp.Tests.Testing;

/// <summary>
///     Covers <see cref="RecordingCqrsDispatcher" />, the fake dispatcher shipped in CQRSharp.Testing: recording,
///     stubbing, defaults for unstubbed messages, and the argument contract it shares with the real dispatcher.
/// </summary>
public sealed class RecordingCqrsDispatcherTests
{
    private readonly RecordingCqrsDispatcher _dispatcher = new();

    [Fact]
    public async Task An_unstubbed_plain_command_succeeds_and_is_recorded()
    {
        var command = new RenameUser(7, "Ada");

        var result = await _dispatcher.Send(command, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _dispatcher.SentRequests.Should().ContainSingle().Which.Should().BeSameAs(command);
        _dispatcher.Sent<RenameUser>().Should().ContainSingle().Which.Name.Should().Be("Ada");
    }

    [Fact]
    public void An_unstubbed_query_throws_at_the_call_site_naming_the_request_type()
    {
        var act = () => _dispatcher.Send(new GetUserName(7));

        AssertThrowsSynchronously<InvalidOperationException>(() => act())
            .Message.Should().Match($"*{typeof(GetUserName).FullName}*Setup*");
        // The attempt is still recorded, so a test can assert on what the code under test tried to send.
        _dispatcher.Sent<GetUserName>().Should().ContainSingle();
    }

    [Fact]
    public void An_unstubbed_value_returning_command_throws_rather_than_inventing_a_value()
    {
        var act = () => _dispatcher.Send(new CreateUser("Ada"));

        AssertThrowsSynchronously<InvalidOperationException>(() => act())
            .Message.Should().Match($"*{typeof(CreateUser).FullName}*");
    }

    [Fact]
    public async Task Setup_with_a_function_answers_from_the_request()
    {
        _dispatcher.Setup<GetUserName, string>(query => $"user-{query.Id}");

        (await _dispatcher.Send(new GetUserName(1), TestContext.Current.CancellationToken)).Should().Be("user-1");
        (await _dispatcher.Send(new GetUserName(2), TestContext.Current.CancellationToken)).Should().Be("user-2");
    }

    [Fact]
    public async Task Setup_with_a_fixed_value_answers_every_send_and_can_fail_a_command()
    {
        _dispatcher
            .Setup<GetUserName, string>("Ada")
            .Setup<RenameUser, CommandResult>(CommandResult.FromError("taken", 409));

        (await _dispatcher.Send(new GetUserName(1), TestContext.Current.CancellationToken)).Should().Be("Ada");
        var result = await _dispatcher.Send(new RenameUser(1, "Ada"), TestContext.Current.CancellationToken);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(409);
    }

    [Fact]
    public async Task The_latest_setup_for_a_type_wins()
    {
        _dispatcher.Setup<GetUserName, string>("first").Setup<GetUserName, string>("second");

        (await _dispatcher.Send(new GetUserName(1), TestContext.Current.CancellationToken)).Should().Be("second");
    }

    [Fact]
    public async Task SetupAsync_receives_the_callers_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        _dispatcher.SetupAsync<GetUserName, string>((_, token) =>
        {
            seen = token;
            return Task.FromResult("Ada");
        });

        await _dispatcher.Send(new GetUserName(1), cts.Token);

        seen.Should().Be(cts.Token);
    }

    [Fact]
    public async Task A_stub_on_a_base_request_covers_a_derived_request()
    {
        _dispatcher.Setup<GetUserName, string>("base");

        (await _dispatcher.Send(new GetUserNameVerbose(1), TestContext.Current.CancellationToken)).Should().Be("base");
    }

    [Fact]
    public async Task The_untyped_send_uses_the_same_stubs_and_defaults()
    {
        _dispatcher.Setup<GetUserName, string>("Ada");

        (await _dispatcher.Send((object)new GetUserName(1), TestContext.Current.CancellationToken)).Should().Be("Ada");
        (await _dispatcher.Send((object)new RenameUser(1, "Ada"), TestContext.Current.CancellationToken)).Should().BeOfType<CommandResult>()
            .Which.IsSuccess.Should().BeTrue();
        _dispatcher.SentRequests.Should().HaveCount(2);
    }

    [Fact]
    public void The_untyped_overloads_reject_the_same_arguments_as_the_real_dispatcher()
    {
        var sendNonRequest = () => _dispatcher.Send(new object());
        var sendStream = () => _dispatcher.Send((object)new CountTo(3));
        var streamNonStream = () => _dispatcher.Stream((object)new GetUserName(1));

        AssertThrowsSynchronously<ArgumentException>(() => sendNonRequest());
        AssertThrowsSynchronously<InvalidOperationException>(() => sendStream()).Message.Should().Match("*Stream(...)*");
        streamNonStream.Should().Throw<ArgumentException>();
        _dispatcher.Dispatched.Should().BeEmpty("rejected arguments were never dispatched");
    }

    [Fact]
    public async Task Throws_surfaces_through_the_send_task_not_at_the_call_site()
    {
        _dispatcher.Throws<RenameUser>(command => new InvalidOperationException($"cannot rename {command.Id}"));

        var task = _dispatcher.Send(new RenameUser(7, "Ada"), TestContext.Current.CancellationToken);

        // Reaching this line at all proves Send did not throw synchronously; the failure lives in the task.
        var act = () => task;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("cannot rename 7");
        _dispatcher.Sent<RenameUser>().Should().ContainSingle();
    }

    [Fact]
    public async Task Throws_takes_precedence_over_a_response_stub()
    {
        _dispatcher.Setup<GetUserName, string>("Ada").Throws<GetUserName>(new TimeoutException());

        var act = () => _dispatcher.Send(new GetUserName(1));

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task A_stub_that_throws_faults_the_task()
    {
        _dispatcher.Setup<GetUserName, string>(_ => throw new KeyNotFoundException());

        var task = _dispatcher.Send(new GetUserName(1), TestContext.Current.CancellationToken);

        var act = () => task;
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task SetupStream_streams_the_items_typed_and_untyped()
    {
        _dispatcher.SetupStream<CountTo, int>(request => Enumerable.Range(1, request.Max));

        var typed = await ToListAsync(_dispatcher.Stream(new CountTo(3), TestContext.Current.CancellationToken));
        var boxed = await ToListAsync(_dispatcher.Stream((object)new CountTo(2), TestContext.Current.CancellationToken));

        typed.Should().Equal(1, 2, 3);
        boxed.Should().Equal(1, 2);
        _dispatcher.StartedStreams.Should().HaveCount(2);
        _dispatcher.Streamed<CountTo>().Select(r => r.Max).Should().Equal(3, 2);
    }

    [Fact]
    public async Task SetupStream_accepts_an_async_sequence_and_passes_the_token()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;
        _dispatcher.SetupStream<CountTo, int>((request, token) =>
        {
            seen = token;
            return Count(request.Max);
        });

        (await ToListAsync(_dispatcher.Stream(new CountTo(2), cts.Token))).Should().Equal(1, 2);
        seen.Should().Be(cts.Token);
    }

    [Fact]
    public async Task A_cancelled_in_memory_stream_stops_between_items()
    {
        using var cts = new CancellationTokenSource();
        _dispatcher.SetupStream<CountTo, int>(request => Enumerable.Range(1, request.Max));

        var act = async () =>
        {
            await foreach (var _ in _dispatcher.Stream(new CountTo(10), cts.Token))
                await cts.CancelAsync();
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void An_unstubbed_stream_throws_naming_the_request_type()
    {
        var act = () => _dispatcher.Stream(new CountTo(3));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{typeof(CountTo).FullName}*SetupStream*");
        _dispatcher.Streamed<CountTo>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_failing_stream_throws_on_enumeration()
    {
        _dispatcher.Throws<CountTo>(new TimeoutException());

        var stream = _dispatcher.Stream(new CountTo(3), TestContext.Current.CancellationToken);
        var act = () => ToListAsync(stream);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Publish_records_notifications_and_can_be_made_to_fail()
    {
        await _dispatcher.Publish(new UserRenamed(7), TestContext.Current.CancellationToken);
        _dispatcher.Throws<UserDeleted>(new InvalidOperationException("handler failed"));

        var act = () => _dispatcher.Publish(new UserDeleted(7));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler failed");
        _dispatcher.PublishedNotifications.Should().HaveCount(2);
        _dispatcher.Published<UserRenamed>().Should().ContainSingle().Which.Id.Should().Be(7);
        _dispatcher.Published<INotification>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Dispatched_keeps_one_ordered_log_across_all_operations_and_ClearRecorded_keeps_stubs()
    {
        _dispatcher.Setup<GetUserName, string>("Ada").SetupStream<CountTo, int>(_ => [1]);

        await _dispatcher.Send(new GetUserName(1), TestContext.Current.CancellationToken);
        await _dispatcher.Publish(new UserRenamed(1), TestContext.Current.CancellationToken);
        _ = _dispatcher.Stream(new CountTo(1), TestContext.Current.CancellationToken);
        await _dispatcher.Send(new RenameUser(1, "Ada"), TestContext.Current.CancellationToken);

        _dispatcher.Dispatched.Select(entry => entry.Kind).Should().Equal(
            DispatchKind.Send, DispatchKind.Publish, DispatchKind.Stream, DispatchKind.Send);
        _dispatcher.Dispatched.Select(entry => entry.Message.GetType()).Should().Equal(
            typeof(GetUserName), typeof(UserRenamed), typeof(CountTo), typeof(RenameUser));

        _dispatcher.ClearRecorded();

        _dispatcher.Dispatched.Should().BeEmpty();
        (await _dispatcher.Send(new GetUserName(1), TestContext.Current.CancellationToken)).Should().Be("Ada");
    }

    [Fact]
    public async Task Recording_is_thread_safe()
    {
        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            await _dispatcher.Send(new RenameUser(i, "x"));
            await _dispatcher.Publish(new UserRenamed(i));
        })));

        _dispatcher.Sent<RenameUser>().Select(c => c.Id).Should().BeEquivalentTo(Enumerable.Range(0, 64));
        _dispatcher.Published<UserRenamed>().Should().HaveCount(64);
        _dispatcher.Dispatched.Should().HaveCount(128);
    }

    [Fact]
    public async Task Null_arguments_are_rejected()
    {
        var send = () => _dispatcher.Send<string>(null!);
        var publish = () => _dispatcher.Publish<UserRenamed>(null!);
        var setup = () => _dispatcher.Setup<GetUserName, string>((Func<GetUserName, string>)null!);

        await send.Should().ThrowAsync<ArgumentNullException>();
        await publish.Should().ThrowAsync<ArgumentNullException>();
        setup.Should().Throw<ArgumentNullException>();
    }

    // Send returns a Task, so FluentAssertions only offers ThrowAsync for it, which cannot tell a synchronous throw
    // from a faulted task. These tests care about exactly that difference.
    private static TException AssertThrowsSynchronously<TException>(Action act)
        where TException : Exception
        => Assert.Throws<TException>(act);

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }

    private static async IAsyncEnumerable<int> Count(int max)
    {
        for (var i = 1; i <= max; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    // Message fixtures only. The recording dispatcher runs no handlers, so none exist for these requests. They are
    // private so the source generator (which runs on this project) does not see them as requests missing a handler.
    private sealed class RenameUser(int id, string name) : CommandBase
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
    }

    private sealed class CreateUser(string name) : ResultCommandBase<int>
    {
        public string Name { get; } = name;
    }

    private class GetUserName(int id) : QueryBase<string>
    {
        public int Id { get; } = id;
    }

    private sealed class GetUserNameVerbose(int id) : GetUserName(id);

    private sealed class CountTo(int max) : StreamRequestBase<int>
    {
        public int Max { get; } = max;
    }

    private sealed record UserRenamed(int Id) : INotification;

    private sealed record UserDeleted(int Id) : INotification;
}

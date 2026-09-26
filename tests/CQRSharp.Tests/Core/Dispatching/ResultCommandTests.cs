using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     End-to-end tests for value-returning commands: a command that implements <see cref="ICommand{TResult}" /> is
///     handled by an <see cref="IResultCommandHandler{TCommand, TResult}" /> and dispatched through the generated
///     pipeline, returning a <see cref="CommandResult{TResult}" /> that carries the minted value on success.
/// </summary>
public sealed class ResultCommandTests
{
    private static ICqrsDispatcher Dispatcher(out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        provider = services.BuildServiceProvider();
        return provider.CreateScope().ServiceProvider.GetRequiredService<ICqrsDispatcher>();
    }

    [Fact]
    public async Task Send_round_trips_the_minted_value_on_success()
    {
        var cqrs = Dispatcher(out var provider);
        await using var _ = provider;

        CommandResult<string> result = await cqrs.Send(new MintSecret { Seed = "abc" }, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("secret:abc");
    }

    [Fact]
    public async Task Send_returns_a_failed_result_without_a_value()
    {
        var cqrs = Dispatcher(out var provider);
        await using var _ = provider;

        var result = await cqrs.Send(new MintSecret { Seed = "" }, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().BeNull();
        result.ErrorCode.Should().Be(400);
    }

    [Fact]
    public async Task Untyped_send_returns_the_boxed_command_result()
    {
        var cqrs = Dispatcher(out var provider);
        await using var _ = provider;

        object? result = await cqrs.Send((object)new MintSecret { Seed = "xyz" }, TestContext.Current.CancellationToken);

        result.Should().BeOfType<CommandResult<string>>()
            .Which.Value.Should().Be("secret:xyz");
    }

    [Fact(DisplayName = "A value-returning command whose handler returns null fails instead of handing the caller null")]
    public async Task Null_command_result_of_T_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new NullMintCommand());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned null*");
    }
}

/// <summary>A value-returning command: mints a one-time secret that no query could return.</summary>
public sealed class MintSecret : ResultCommandBase<string>
{
    public required string Seed { get; init; }
}

/// <summary>Handler for <see cref="MintSecret" />.</summary>
public sealed class MintSecretHandler : IResultCommandHandler<MintSecret, string>
{
    public Task<CommandResult<string>> Handle(MintSecret command, CancellationToken cancellationToken)
        => Task.FromResult(string.IsNullOrEmpty(command.Seed)
            ? CommandResult<string>.FromError("seed required", 400)
            : CommandResult<string>.FromSuccess($"secret:{command.Seed}"));
}

public sealed class NullMintCommand : ResultCommandBase<string>;

public sealed class NullMintCommandHandler : IResultCommandHandler<NullMintCommand, string>
{
    public Task<CommandResult<string>> Handle(NullMintCommand command, CancellationToken cancellationToken)
        => Task.FromResult<CommandResult<string>>(null!);
}

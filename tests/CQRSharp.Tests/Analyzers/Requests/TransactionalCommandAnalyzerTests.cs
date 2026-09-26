using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>CQRA015: <c>ITransactionalCommand</c> on a request that is not a command.</summary>
public sealed class TransactionalCommandAnalyzerTests
{
    [Theory(DisplayName = "CQRA015: a query, a stream or a plain request carrying ITransactionalCommand is flagged")]
    [InlineData("public sealed class Target : QueryBase<int>, ITransactionalCommand")]
    [InlineData("public sealed class Target : StreamRequestBase<int>, ITransactionalCommand")]
    [InlineData("public sealed class Target : IRequest<int>, ITransactionalCommand")]
    [InlineData("public sealed record Target : IQuery<int>, ITransactionalCommand")]
    public async Task Non_command_is_flagged(string declaration)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync($$"""
            using System.Data;
            using CQRSharp;

            {{declaration}}
            {
                public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
                public IRequestContext? Context { get; set; }
            }
            """, new TransactionalCommandAnalyzer());

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA015" && d.Severity == DiagnosticSeverity.Warning)
            .Which.GetMessage().Should().Contain("'Target'");
    }

    [Theory(DisplayName = "CQRA015: a command, a value-returning command or an abstract base is not flagged")]
    [InlineData("public sealed class Target : CommandBase, ITransactionalCommand")]
    [InlineData("public sealed class Target : ResultCommandBase<int>, ITransactionalCommand")]
    [InlineData("public sealed class Target : ICommand, ITransactionalCommand")]
    [InlineData("public abstract class Target : ITransactionalCommand")]
    public async Task Command_is_not_flagged(string declaration)
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync($$"""
            using System.Data;
            using CQRSharp;

            {{declaration}}
            {
                public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
                public IRequestContext? Context { get; set; }
            }
            """, new TransactionalCommandAnalyzer());

        diagnostics.Should().NotContain(d => d.Id == "CQRA015");
    }
}

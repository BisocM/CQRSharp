using System.Collections.Immutable;
using CQRSharp.Analyzers;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.CodeAnalysis;

namespace CQRSharp.Tests.Analyzers;

/// <summary>
///     CQRA012: a validator in a project whose every CQRSharp registration leaves the validation behavior out. A call of
///     the builder overload adds it unless its configuration calls UseValidation(false); the parameterless overload adds
///     no behavior. Registrations and the opt-out are recognized by the methods they bind to, so each case runs the
///     generators and calls the real generated <c>AddCqrsGenerated</c> and the real builder.
/// </summary>
public sealed class ValidatorWithoutValidationAnalyzerTests
{
    private const string Validator = """
                                     using System;
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using CQRSharp;
                                     using CQRSharp.Pipelines;
                                     using Microsoft.Extensions.DependencyInjection;

                                     public sealed class MyCommand : CommandBase;
                                     public sealed class MyCommandHandler : ICommandHandler<MyCommand>
                                     {
                                         public Task<CommandResult> Handle(MyCommand command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
                                     }
                                     public sealed class MyValidator : IRequestValidator<MyCommand>
                                     {
                                         public Task<ValidationFailure[]> ValidateAsync(MyCommand request, CancellationToken cancellationToken)
                                             => Task.FromResult(Array.Empty<ValidationFailure>());
                                     }

                                     """;

    [Theory(DisplayName = "CQRA012: a validator where CQRSharp is registered without the validation behavior is flagged")]
    [InlineData("services.AddCqrs()")]
    [InlineData("services.AddCqrsGenerated(b => b.UseValidation(false))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseLogging().UseValidation(false))")]
    [InlineData("services.AddCqrsGenerated(b => { b.UseValidation(false); b.UseTimeout(o => { }); })")]
    public async Task Registration_without_the_validation_behavior_is_flagged(string registration)
    {
        var diagnostics = await AnalyzeAsync(Startup(registration));

        diagnostics.Should().ContainSingle(d => d.Id == "CQRA012");
    }

    [Theory(DisplayName = "CQRA012: a builder call, and the parameterless one, add the validation behavior unless it is turned off")]
    [InlineData("services.AddCqrsGenerated()")]
    [InlineData("services.AddCqrsGenerated(b => { })")]
    [InlineData("services.AddCqrsGenerated(b => b.ValidateOnStart())")]
    [InlineData("services.AddCqrsGenerated(b => b.UseValidation())")]
    [InlineData("services.AddCqrsGenerated(b => b.UseValidation(false).UseValidation(true))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseTimeout(o => { }))")]
    [InlineData("services.AddCqrsGenerated(b => b.UseFluentValidation())")]
    public async Task Builder_call_is_not_flagged(string registration)
    {
        var diagnostics = await AnalyzeAsync(Startup(registration));

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Theory(DisplayName = "CQRA012: a UseValidation(false) that may not run proves nothing, so validation stays on")]
    [InlineData("services.AddCqrsGenerated(b => { if (Environment.UserInteractive) b.UseValidation(false); })")]
    [InlineData("services.AddCqrsGenerated(b => _ = Environment.UserInteractive ? b.UseValidation(false) : b)")]
    [InlineData("services.AddCqrsGenerated(b => { void Off() => b.UseValidation(false); })")]
    [InlineData("services.AddCqrsGenerated(b => { Action off = () => b.UseValidation(false); })")]
    [InlineData("services.AddCqrsGenerated(b => { for (var i = 0; i < 0; i++) b.UseValidation(false); })")]
    public async Task Conditional_opt_out_is_not_flagged(string registration)
    {
        var diagnostics = await AnalyzeAsync(Startup(registration));

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Theory(DisplayName = "CQRA012: builder calls add up, so one that keeps the validation behavior is enough")]
    [InlineData("services.AddCqrs()")]
    [InlineData("services.AddCqrsGenerated(b => b.UseValidation(false))")]
    public async Task One_builder_call_with_validation_is_enough(string other)
    {
        var diagnostics = await AnalyzeAsync(Validator +
            "public static class Startup { public static void Configure(IServiceCollection services) { " + other +
            "; services.AddCqrsGenerated(b => b.UseLogging()); } }\n");

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA012: a configuration the analyzer cannot see into counts as keeping the validation behavior")]
    public async Task Opaque_configuration_is_not_flagged()
    {
        var diagnostics = await AnalyzeAsync(Validator + """
            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddCqrsGenerated(Wire);
                private static void Wire(ICqrsBuilder builder) => builder.UseValidation(false);
            }
            """);

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA012: an unrelated method named UseValidation does not turn the validation behavior off")]
    public async Task Unrelated_method_named_like_the_opt_out_does_not_count()
    {
        var diagnostics = await AnalyzeAsync(Validator + """
            public static class AppExtensions
            {
                public static ICqrsBuilder UseValidation(this ICqrsBuilder builder, string mode) => builder;
            }
            public static class Startup
            {
                public static void Configure(IServiceCollection services) => services.AddCqrsGenerated(b => b.UseValidation("off"));
            }
            """);

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA012: an unrelated method named AddCqrs does not make a library a composition root")]
    public async Task Unrelated_method_named_like_a_registration_does_not_count()
    {
        var diagnostics = await AnalyzeAsync(Validator + """
            public static class LibraryExtensions
            {
                public static string AddCqrs(this string name) => name;
                public static string Name() => "x".AddCqrs();
            }
            """);

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA012: a builder extension declared in the project leaves the validation behavior on")]
    public async Task Own_builder_extension_keeps_validation()
    {
        const string extensions = """
                                  public static class BuilderExtensions
                                  {
                                      public static ICqrsBuilder UseMyDefaults(this ICqrsBuilder builder) => builder.ValidateOnStart();
                                  }
                                  """;

        var diagnostics = await AnalyzeAsync(Startup("services.AddCqrsGenerated(b => b.UseMyDefaults())") + extensions);

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    [Fact(DisplayName = "CQRA012: a library that declares validators but does not register CQRSharp is not flagged")]
    public async Task Library_without_registration_is_not_flagged()
    {
        var diagnostics = await AnalyzeAsync(Validator);

        diagnostics.Should().NotContain(d => d.Id == "CQRA012");
    }

    private static string Startup(string registration)
        => Validator + "public static class Startup { public static void Configure(IServiceCollection services) => " + registration + "; }\n";

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
        => CompilationHarness.AnalyzeWithGeneratorsAsync(source, new ValidatorWithoutValidationAnalyzer());
}

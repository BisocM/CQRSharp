namespace CQRSharp.Generators.SourceGeneration;

/// <summary>
///     Metadata names for <c>CQRSharp.Core</c> types needed during source generation.
/// </summary>
/// <remarks>
///     These live in the generator — which already targets Core's shape — rather than in the shipped
///     <c>CQRSharp.Abstractions</c> package's <see cref="TypeStrings" />, so the
///     Domain layer does not publicly hardcode (and ship) Core type names.
/// </remarks>
internal static class CoreTypeStrings
{
    public const string IPipelineBehavior = "CQRSharp.Core.Pipelines.IPipelineBehavior`2";
    public const string IStreamPipelineBehavior = "CQRSharp.Core.Pipelines.IStreamPipelineBehavior`2";
    public const string IRequestContextFactory = "CQRSharp.Core.Factories.IRequestContextFactory`1";
}

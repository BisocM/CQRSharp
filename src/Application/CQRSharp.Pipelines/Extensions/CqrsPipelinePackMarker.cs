namespace CQRSharp.Pipelines.Extensions;

/// <summary>
///     Sentinel type registered once by <c>AddCqrsPipelinePack</c>. Its presence in the service collection marks that
///     the pack has already run, letting a repeated call short-circuit so behaviors are not registered (and thus
///     stacked) twice.
/// </summary>
internal sealed class CqrsPipelinePackMarker;

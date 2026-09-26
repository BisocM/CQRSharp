namespace CQRSharp.Sample.Application.Queries.Requests;

/// <summary>Returns the id of the DI scope its handler runs in, which tells which scope a dispatch executed in.</summary>
public sealed class ScopeProbeQuery : QueryBase<Guid>;

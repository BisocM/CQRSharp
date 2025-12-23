using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Pipelines.Options;

namespace CQRSharp.Pipelines.Extensions;

public sealed class CqrsPipelinePackOptions
{
    public bool IncludeExceptionHandling { get; set; } = true;

    public bool IncludeValidation { get; set; } = true;

    public Func<IServiceProvider, IUnitOfWork>? UnitOfWorkFactory { get; set; }
    public Action<UnitOfWorkOptions>? ConfigureUnitOfWork { get; set; }

    public Action<RateLimiterOptions>? ConfigureRateLimiting { get; set; }
    public Action<ResilienceOptions>? ConfigureResilience { get; set; }
    public Action<TimeoutOptions>? ConfigureTimeout { get; set; }
}

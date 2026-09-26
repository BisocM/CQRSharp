using CQRSharp.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     The first-use half of <c>CQRCONF005</c> / <c>CQRCONF006</c> for one request type in one service provider: whether
///     the behaviors its markers depend on (<see cref="IIdempotentRequest" /> on idempotency, <see cref="IRetryableRequest" />
///     on resilience) are registered for it, decided once, by the rule the startup validator applies, from the behaviors its
///     first dispatch resolves. A missing idempotency behavior is kept as a failure that every dispatch of the request then
///     throws, since running it would process duplicates; a missing resilience behavior is logged once, since running it
///     only forgoes retries.
/// </summary>
/// <remarks>
///     The check reads the behaviors the dispatch resolves anyway, so it constructs nothing of its own; a request without
///     markers gets no check at all (<see cref="For" /> returns <see langword="null" />).
/// </remarks>
internal sealed class MarkerBehaviorCheck
{
    private readonly Type _requestType;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private volatile bool _checked;
    private string? _failure;

    private MarkerBehaviorCheck(Type requestType, ILogger logger)
    {
        _requestType = requestType;
        _logger = logger;
    }

    /// <summary>
    ///     The check for <paramref name="requestType" />, or <see langword="null" /> when it carries no marker. When the
    ///     provider proves no behavior is registered for the request at all, the verdict is reached here, on the empty set.
    /// </summary>
    /// <param name="requestType">The request type.</param>
    /// <param name="mayHaveBehaviors"><see langword="false" /> when the provider proves the request has no behavior.</param>
    /// <param name="logger">Where a warning is logged.</param>
    public static MarkerBehaviorCheck? For(Type requestType, bool mayHaveBehaviors, ILogger logger)
    {
        if (!CqrsConfigurationRules.HasBehaviorMarkers(requestType)) return null;

        var check = new MarkerBehaviorCheck(requestType, logger);
        if (!mayHaveBehaviors) check.Decide(ReadOnlySpan<object>.Empty);
        return check;
    }

    /// <summary>
    ///     Throws the request's configuration error, deciding it first from <paramref name="resolved" />, every behavior
    ///     registered for the request (exempted ones included), when this is the first dispatch.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request's marker needs a behavior that is not registered.</exception>
    public void Verify<TBehavior>(ReadOnlySpan<TBehavior> resolved) where TBehavior : class
    {
        if (!_checked) Decide(resolved);
        if (_failure is { } failure) throw new InvalidOperationException(failure);
    }

    private void Decide<TBehavior>(ReadOnlySpan<TBehavior> resolved) where TBehavior : class
    {
        lock (_gate)
        {
            if (_checked) return;

            var wired = new Type[resolved.Length];
            for (var i = 0; i < resolved.Length; i++) wired[i] = resolved[i].GetType();

            foreach (var issue in CqrsConfigurationRules.MissingMarkerBehaviors(_requestType, wired))
                if (issue.Severity == CqrsBindingIssueSeverity.Error)
                    _failure = CqrsConfigurationRules.FailureMessage(issue);
                else
                    CqrsConfigurationLog.RequestWarning(_logger, issue.Code, _requestType.Name, issue.Message);

            // Published last (a volatile write): a dispatch that sees the check done also sees its failure.
            _checked = true;
        }
    }
}

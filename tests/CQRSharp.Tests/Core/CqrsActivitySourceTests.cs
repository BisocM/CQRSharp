using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Verifies the core activity source emits named request spans when a listener is subscribed (and is a no-op
///     otherwise).
/// </summary>
public class CqrsActivitySourceTests
{
    [Fact(DisplayName = "StartRequest emits a named span when a listener is subscribed")]
    public void StartRequest_EmitsNamedSpan_WhenListening()
    {
        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsActivitySource.Name,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add
        };
        ActivitySource.AddActivityListener(listener);

        using (CqrsActivitySource.StartRequest("CQRS Command", typeof(string)))
        {
        }

        started.Should().ContainSingle(a => a.OperationName == "CQRS Command String"
                                            && a.GetTagItem("cqrsharp.request_type") as string == typeof(string).FullName);
    }

    [Fact(DisplayName = "StartRequest is a no-op (null) when no listener is subscribed")]
    public void StartRequest_NoListener_ReturnsNull()
    {
        // No ActivityListener for the CQRSharp source in this test -> StartActivity returns null (zero overhead).
        CqrsActivitySource.StartRequest("CQRS Command", typeof(string)).Should().BeNull();
    }
}
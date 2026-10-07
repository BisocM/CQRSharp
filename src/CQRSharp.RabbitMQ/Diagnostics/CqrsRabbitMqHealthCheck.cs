using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CQRSharp.RabbitMQ;

/// <summary>
///     Reports every RabbitMQ transport of the application: the failure status while a connection is not open, the
///     publisher's exchanges could not be declared, or a consumer's topology or subscription is refused; degraded while the
///     broker blocks a connection or a consumer is between channels; healthy otherwise. Each connection's and consumer's
///     state is in the result's data.
/// </summary>
internal sealed class CqrsRabbitMqHealthCheck(IEnumerable<RabbitMqTransportRuntime> transports) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var failures = new List<string>();
        var degradations = new List<string>();
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var any = false;

        foreach (var transport in transports.Distinct())
        {
            any = true;
            Link(transport.Name, transport.Publisher);
            if (transport.Consumer is { } consumer && !ReferenceEquals(consumer, transport.Publisher))
                Link(transport.Name, consumer);

            if (transport.PublisherTopologyFailure is { } topology)
                failures.Add($"{transport.Name}: {topology}");

            foreach (var queue in transport.Consumers)
            {
                data[$"{transport.Name}.queue.{queue.Queue}"] = queue.Status.ToString();
                switch (queue.Status)
                {
                    case RabbitMqConsumerStatus.Refused:
                        failures.Add($"{transport.Name}: queue '{queue.Queue}' cannot be consumed: {queue.StatusDetail}");
                        break;
                    case RabbitMqConsumerStatus.Starting or RabbitMqConsumerStatus.Interrupted:
                        degradations.Add($"{transport.Name}: queue '{queue.Queue}' is not being consumed{(queue.StatusDetail is { } detail ? $" ({detail})" : "")}");
                        break;
                }
            }
        }

        if (!any)
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, "No RabbitMQ transport is registered."));
        if (failures.Count > 0)
            return Task.FromResult(new HealthCheckResult(context.Registration.FailureStatus, string.Join("; ", failures) + ".", data: data));
        if (degradations.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(string.Join("; ", degradations) + ".", data: data));
        return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ connections are open and every queue is consumed.", data));

        void Link(string name, RabbitMqLink link)
        {
            var key = $"{name}.{link.Role}";
            if (link.Current is null)
            {
                data[key] = "closed";
                failures.Add($"{name}: the {link.Role} connection is not open");
            }
            else if (link.BlockedReason is { } blocked)
            {
                data[key] = "blocked";
                degradations.Add($"{name}: the broker blocks the {link.Role} connection ({blocked})");
            }
            else
            {
                data[key] = "open";
            }
        }
    }
}

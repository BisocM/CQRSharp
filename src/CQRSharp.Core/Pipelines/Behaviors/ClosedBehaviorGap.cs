using System.ComponentModel;
using CQRSharp.Pipelines;

namespace CQRSharp.Core.Pipelines;

/// <summary>
///     An open-generic behavior that applies to a request whose result (or streamed item) is a value type, or to a
///     value-type notification, but that the generated code in view of both could not close: it is internal to an
///     assembly that does not grant access, nested in a generic type, marked <c>[Obsolete]</c> as an error, or the request
///     or notification itself cannot be named there. Without dynamic code (Native AOT) the container cannot close it
///     either, so a registration of it has no <see cref="ClosedBehaviorFactory" /> for that target; the gap lets the
///     runtime refuse that registration instead of silently leaving the behavior out. The behavior, which generated code
///     cannot name, is identified by its runtime name, and so is the target when generated code cannot name it either.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ClosedBehaviorGap
{
    /// <summary>A gap for a request or notification generated code can name.</summary>
    /// <param name="serviceType">
    ///     The closed behavior interface, e.g. <c>typeof(IPipelineBehavior&lt;GetCount, int&gt;)</c> or
    ///     <c>typeof(INotificationPipelineBehavior&lt;Tick&gt;)</c>.
    /// </param>
    /// <param name="behaviorTypeName">The open behavior's <see cref="Type.FullName" />, e.g. <c>Lib.TenantBehavior`2</c>.</param>
    /// <param name="behaviorAssemblyName">The simple name of the assembly the behavior is declared in.</param>
    /// <param name="reason">Why generated code could not close it, completing "because ...".</param>
    public ClosedBehaviorGap(Type serviceType, string behaviorTypeName, string behaviorAssemblyName, string reason)
    {
        ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        if (!serviceType.IsConstructedGenericType)
            throw new ArgumentException("The service type must be a closed behavior interface.", nameof(serviceType));

        ServiceDefinition = serviceType.GetGenericTypeDefinition();
        var target = serviceType.GetGenericArguments()[0];
        TargetTypeName = target.FullName ?? target.Name;
        TargetAssemblyName = target.Assembly.GetName().Name ?? string.Empty;
        BehaviorTypeName = behaviorTypeName ?? throw new ArgumentNullException(nameof(behaviorTypeName));
        BehaviorAssemblyName = behaviorAssemblyName ?? throw new ArgumentNullException(nameof(behaviorAssemblyName));
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    /// <summary>A gap for a request or notification generated code cannot name.</summary>
    /// <param name="serviceDefinition">
    ///     The open behavior interface that wraps the target: <c>typeof(IPipelineBehavior&lt;,&gt;)</c>,
    ///     <c>typeof(IStreamPipelineBehavior&lt;,&gt;)</c> or <c>typeof(INotificationPipelineBehavior&lt;&gt;)</c>.
    /// </param>
    /// <param name="targetTypeName">The request's or notification's <see cref="Type.FullName" />.</param>
    /// <param name="targetAssemblyName">The simple name of the assembly the request or notification is declared in.</param>
    /// <param name="behaviorTypeName">The open behavior's <see cref="Type.FullName" />.</param>
    /// <param name="behaviorAssemblyName">The simple name of the assembly the behavior is declared in.</param>
    /// <param name="reason">Why generated code could not close it, completing "because ...".</param>
    public ClosedBehaviorGap(
        Type serviceDefinition,
        string targetTypeName,
        string targetAssemblyName,
        string behaviorTypeName,
        string behaviorAssemblyName,
        string reason)
    {
        ServiceDefinition = serviceDefinition ?? throw new ArgumentNullException(nameof(serviceDefinition));
        if (!serviceDefinition.IsGenericTypeDefinition)
            throw new ArgumentException("The service definition must be an open behavior interface.", nameof(serviceDefinition));

        TargetTypeName = targetTypeName ?? throw new ArgumentNullException(nameof(targetTypeName));
        TargetAssemblyName = targetAssemblyName ?? throw new ArgumentNullException(nameof(targetAssemblyName));
        BehaviorTypeName = behaviorTypeName ?? throw new ArgumentNullException(nameof(behaviorTypeName));
        BehaviorAssemblyName = behaviorAssemblyName ?? throw new ArgumentNullException(nameof(behaviorAssemblyName));
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    internal Type? ServiceType { get; }
    internal Type ServiceDefinition { get; }
    internal string TargetTypeName { get; }
    internal string TargetAssemblyName { get; }
    internal string BehaviorTypeName { get; }
    internal string BehaviorAssemblyName { get; }
    internal string Reason { get; }

    private bool IsNotificationGap => ServiceDefinition == typeof(INotificationPipelineBehavior<>);

    /// <summary>
    ///     Whether this gap is <paramref name="openBehaviorType" /> (an open-generic registration's implementation type)
    ///     for targets resolved as <paramref name="serviceType" /> (a closed behavior interface).
    /// </summary>
    internal bool Matches(Type serviceType, Type openBehaviorType)
    {
        if (!serviceType.IsConstructedGenericType || !IsNamed(openBehaviorType, BehaviorTypeName, BehaviorAssemblyName)) return false;
        if (ServiceType is not null) return ServiceType == serviceType;

        return serviceType.GetGenericTypeDefinition() == ServiceDefinition &&
               IsNamed(serviceType.GetGenericArguments()[0], TargetTypeName, TargetAssemblyName);
    }

    /// <summary>The error a dispatch of the request, or a publish of the notification, fails with while the behavior is registered.</summary>
    internal InvalidOperationException ToException()
        => IsNotificationGap
            ? new InvalidOperationException(
                $"The notification pipeline behavior '{BehaviorTypeName}' ({BehaviorAssemblyName}) is registered and applies to the " +
                $"notification '{TargetTypeName}' ({TargetAssemblyName}), which is a value type, but generated code could not close it " +
                $"over that notification because {Reason}. Without dynamic code (Native AOT) the container cannot close it either, so it " +
                "would silently not run. Make it closable (public, or internal to an assembly that grants the application's " +
                "InternalsVisibleTo; not nested in a generic type; not [Obsolete] as an error), make the notification accessible, or " +
                "stop registering the behavior.")
            : new InvalidOperationException(
                $"The pipeline behavior '{BehaviorTypeName}' ({BehaviorAssemblyName}) is registered and applies to '{TargetTypeName}' " +
                $"({TargetAssemblyName}), whose result is a value type, but generated code could not close it over that request because " +
                $"{Reason}. Without dynamic code (Native AOT) the container cannot close it either, so it would silently not run. Make it " +
                "closable (public, or internal to an assembly that grants the application's InternalsVisibleTo; not nested in a generic " +
                "type; not [Obsolete] as an error), make the request accessible, or stop registering the behavior.");

    private static bool IsNamed(Type type, string fullName, string assemblyName)
        => string.Equals(type.FullName, fullName, StringComparison.Ordinal) &&
           string.Equals(type.Assembly.GetName().Name, assemblyName, StringComparison.Ordinal);
}

/// <summary>One generated module's (or bootstrap's) <see cref="ClosedBehaviorGap" /> entries, registered next to its <see cref="ClosedBehaviorCatalog" />.</summary>
/// <param name="gaps">The gaps.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ClosedBehaviorGapCatalog(IReadOnlyList<ClosedBehaviorGap> gaps)
{
    /// <summary>The gaps, one per (request, open behavior) pair generated code could not close.</summary>
    internal IReadOnlyList<ClosedBehaviorGap> Gaps { get; } = gaps ?? throw new ArgumentNullException(nameof(gaps));
}

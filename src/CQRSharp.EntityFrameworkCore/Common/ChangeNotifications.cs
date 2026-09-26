using System.ComponentModel;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     The property-change notifications the CQRSharp entity types raise. A context built with EF Core's change-tracking
///     proxies (<c>UseChangeTrackingProxies</c>) tracks every entity type of its model through these notifications, and
///     it has to track the rows the stores create with <c>new</c> too, which are not proxies: an entity type that raises
///     them itself is tracked either way, and its proxies add no interception of their own. A context without
///     change-tracking proxies tracks by snapshot and never subscribes, so all a setter costs there is the equality check.
/// </summary>
internal static class ChangeNotifications
{
    /// <summary>Sets <paramref name="field" /> to <paramref name="value" />, raising both notifications around the change; an equal value changes nothing.</summary>
    public static void Set<T>(
        object entity,
        ref T field,
        T value,
        PropertyChangingEventHandler? changing,
        PropertyChangedEventHandler? changed,
        string propertyName)
    {
        // An unchanged value raises nothing: EF Core marks a property modified on every PropertyChanged it receives.
        if (EqualityComparer<T>.Default.Equals(field, value)) return;

        changing?.Invoke(entity, new PropertyChangingEventArgs(propertyName));
        field = value;
        changed?.Invoke(entity, new PropertyChangedEventArgs(propertyName));
    }
}

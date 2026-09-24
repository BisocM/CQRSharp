namespace CQRSharp.Core.Pipelines;

/// <summary>
///     The one total ordering every priority-sorted list uses (behaviors, pre- and post-handlers, notification
///     behaviors): by priority, then by type full name, so equal priorities still order deterministically.
/// </summary>
internal static class PriorityOrdering
{
    public static int Compare(int leftPriority, int rightPriority, object left, object right)
    {
        var byPriority = leftPriority.CompareTo(rightPriority);
        return byPriority != 0
            ? byPriority
            : string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
    }
}

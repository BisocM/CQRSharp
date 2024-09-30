using CQRSharp.Interfaces.Markers;

namespace CQRSharp.Core.Notifications.Types
{
    public sealed class QueryCompletedNotification(string queryName, object? result) : INotification
    {
        public string QueryName { get; } = queryName;
        public object? Result { get; } = result;
    }
}
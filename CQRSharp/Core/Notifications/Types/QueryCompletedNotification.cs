using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Query;

namespace CQRSharp.Core.Notifications.Types
{
    public sealed class QueryCompletedNotification<TResult>(IQuery<TResult> query, object? result) : INotification
    {
        public string QueryName { get; } = query.GetType().Name;
        public object? Result { get; } = result;
    }
}
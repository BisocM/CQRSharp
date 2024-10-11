using CQRSharp.Interfaces.Markers;
using CQRSharp.Interfaces.Markers.Query;

namespace CQRSharp.Core.Notifications.Types
{
    public sealed class QueryInitiatedNotification<TResult>(IQuery<TResult> query) : INotification
    {
        public string QueryName { get; } = query.GetType().Name;
    }
}
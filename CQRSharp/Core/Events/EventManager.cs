using CQRSharp.Core.Events.Types;

namespace CQRSharp.Core.Events
{
    public sealed class EventManager
    {
        public delegate void QueryCompletedEventHandler(object sender, QueryCompletedEventArgs e, CancellationToken cancellationToken);
        public event QueryCompletedEventHandler QueryCompleted = null!;

        private void OnQueryCompleted(QueryCompletedEventArgs e, CancellationToken cancellationToken) => QueryCompleted?.Invoke(this, e, cancellationToken);

        public void TriggerQueryCompleted(string queryName, object? result, CancellationToken cancellationToken)
        {
            var args = new QueryCompletedEventArgs(queryName, result);
            OnQueryCompleted(args, cancellationToken);
        }
    }
}
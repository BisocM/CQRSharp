namespace CQRSharp.Core.Events.Types
{
    public class QueryCompletedEventArgs(string queryName, object? result) : EventArgs
    {
        public string QueryName { get; } = queryName;
        public object? Result { get; } = result;
    }
}
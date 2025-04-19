using System.Threading.Channels;

namespace CQRSharp.Core.Options
{
    /// <summary>
    /// Configuration options for the <see cref="CQRSharp.Core.BackgroundTasks.BackgroundTaskQueue"/>.
    /// </summary>
    public sealed class BackgroundTaskQueueOptions
    {
        /// <summary>
        /// Default number of items to dequeue in one batch when not specified.
        /// </summary>
        internal const int DefaultDequeueBatchSize = 100;

        /// <summary>
        /// Default callback channel capacity if none is provided.
        /// </summary>
        internal const int DefaultCallbackChannelCapacity = 1024;

        /// <summary>
        /// Maximum number of items the queue may hold. Must be &gt; 0.
        /// </summary>
        public int Capacity { get; set; } = 1000;

        /// <summary>
        /// Policy when <see cref="Capacity"/> is reached. Default = <see cref="BoundedChannelFullMode.DropNewest"/>.
        /// </summary>
        public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.DropNewest;

        /// <summary>
        /// How many consumer loops to start. Default = <see cref="Environment.ProcessorCount"/>.
        /// </summary>
        public int ConsumerCount { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Max items to dequeue in one batch. Default = <see cref="DefaultDequeueBatchSize"/>.
        /// </summary>
        public int DequeueBatchSize { get; set; } = DefaultDequeueBatchSize;

        /// <summary>
        /// Callback‑channel queue size to buffer OnEnqueue/OnReject callbacks. Default = <see cref="DefaultCallbackChannelCapacity"/>.
        /// </summary>
        public int CallbackChannelCapacity { get; set; } = DefaultCallbackChannelCapacity;
    }
}
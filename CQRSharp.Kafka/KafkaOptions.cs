namespace CQRSharp.Kafka
{
    public sealed class KafkaOptions
    {
        public string BootstrapServers { get; set; }
        public string CommandTopic { get; set; } = "commands";
        public string QueryTopic { get; set; } = "queries";
        public string EventTopic { get; set; } = "events";
    }
}
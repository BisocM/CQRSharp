using System.Text.Json;

namespace CQRSharp.Kafka
{
    public static class KafkaSerializationSettings
    {
        public static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }
}
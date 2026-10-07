namespace Orders.Api.Messaging;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string OrderEventsTopic { get; set; } = "order-events";

    public string DeadLetterTopic { get; set; } = "order-events.DLT";

    public string ClientId { get; set; } = "orders-api";

    public string SecurityProtocol { get; set; } = "Plaintext";

    public string? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    public string? SaslPassword { get; set; }
}
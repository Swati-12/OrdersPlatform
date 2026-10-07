using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Orders.Api.Messaging;

namespace Orders.Tests;

[TestFixture]
public class KafkaEventPublisherTests
{
    private Mock<IProducer<string, string>> _producer = null!;
    private KafkaEventPublisher _publisher = null!;

    private static readonly KafkaOptions KafkaOptions = new()
    {
        BootstrapServers = "localhost:9092",
        OrderEventsTopic = "order-events",
        DeadLetterTopic = "order-events.DLT",
        ClientId = "orders-api",
        SecurityProtocol = "Plaintext"
    };

    [SetUp]
    public void SetUp()
    {
        _producer = new Mock<IProducer<string, string>>();

        _publisher = new KafkaEventPublisher(
            _producer.Object,
            Options.Create(KafkaOptions),
            NullLogger<KafkaEventPublisher>.Instance);
    }

    [Test]
    public async Task PublishAsync_PublishesMessageToOrderEventsTopic()
    {
        var eventId = Guid.NewGuid();

        var deliveryResult =
            new DeliveryResult<string, string>
            {
                Topic = "order-events",
                Partition = new Partition(0),
                Offset = new Offset(1)
            };

        _producer
            .Setup(p => p.ProduceAsync(
                "order-events",
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deliveryResult);

        await _publisher.PublishAsync(
            eventId,
            "OrderCreated",
            """{"orderId":"123"}""",
            CancellationToken.None);

        _producer.Verify(
            p => p.ProduceAsync(
                "order-events",
                It.Is<Message<string, string>>(m =>
                    m.Key == eventId.ToString() &&
                    m.Value == """{"orderId":"123"}"""),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task PublishAsync_AddsEventHeaders()
    {
        var eventId = Guid.NewGuid();

        var deliveryResult =
            new DeliveryResult<string, string>
            {
                Topic = "order-events",
                Partition = new Partition(0),
                Offset = new Offset(10)
            };

        _producer
            .Setup(p => p.ProduceAsync(
                "order-events",
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deliveryResult);

        await _publisher.PublishAsync(
            eventId,
            "OrderPaid",
            """{"orderId":"456"}""",
            CancellationToken.None);

        _producer.Verify(
            p => p.ProduceAsync(
                "order-events",
                It.Is<Message<string, string>>(m =>
                    GetHeader(m.Headers, "event-id") == eventId.ToString() &&
                    GetHeader(m.Headers, "event-type") == "OrderPaid" &&
                    GetHeader(m.Headers, "content-type") == "application/json"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
   public async Task PublishAsync_WhenKafkaFails_ThrowsException()
{
    var eventId = Guid.NewGuid();

    var failedDeliveryResult = new DeliveryResult<string, string>
    {
        Topic = "order-events",
        Partition = new Partition(0),
        Offset = new Offset(0),
        Message = new Message<string, string>
        {
            Key = eventId.ToString(),
            Value = """{"orderId":"123"}"""
        }
    };

    _producer
        .Setup(p => p.ProduceAsync(
            "order-events",
            It.IsAny<Message<string, string>>(),
            It.IsAny<CancellationToken>()))
        .ThrowsAsync(
            new ProduceException<string, string>(
                new Error(ErrorCode.Local_Transport),
                failedDeliveryResult));

    Assert.ThrowsAsync<ProduceException<string, string>>(
        async () =>
            await _publisher.PublishAsync(
                eventId,
                "OrderCreated",
                """{"orderId":"123"}""",
                CancellationToken.None));
}

    [Test]
    public async Task PublishDeadLetterAsync_PublishesToDltTopic()
    {
        var eventId = Guid.NewGuid();

        var deliveryResult =
            new DeliveryResult<string, string>
            {
                Topic = "order-events.DLT",
                Partition = new Partition(0),
                Offset = new Offset(20)
            };

        _producer
            .Setup(p => p.ProduceAsync(
                "order-events.DLT",
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deliveryResult);

        await _publisher.PublishDeadLetterAsync(
            eventId,
            "OrderCreated",
            """{"orderId":"123"}""",
            "Kafka unavailable",
            CancellationToken.None);

        _producer.Verify(
            p => p.ProduceAsync(
                "order-events.DLT",
                It.Is<Message<string, string>>(m =>
                    m.Key == eventId.ToString() &&
                    m.Value == """{"orderId":"123"}"""),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task PublishDeadLetterAsync_AddsDltHeaders()
    {
        var eventId = Guid.NewGuid();

        var deliveryResult =
            new DeliveryResult<string, string>
            {
                Topic = "order-events.DLT",
                Partition = new Partition(0),
                Offset = new Offset(30)
            };

        _producer
            .Setup(p => p.ProduceAsync(
                "order-events.DLT",
                It.IsAny<Message<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deliveryResult);

        await _publisher.PublishDeadLetterAsync(
            eventId,
            "OrderCreated",
            """{"orderId":"123"}""",
            "Kafka unavailable",
            CancellationToken.None);

        _producer.Verify(
            p => p.ProduceAsync(
                "order-events.DLT",
                It.Is<Message<string, string>>(m =>
                    GetHeader(m.Headers, "event-id") == eventId.ToString() &&
                    GetHeader(m.Headers, "event-type") == "OrderCreated" &&
                    GetHeader(m.Headers, "dead-letter-reason") == "Kafka unavailable" &&
                    GetHeader(m.Headers, "original-topic") == "order-events"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static string? GetHeader(
        Headers headers,
        string key)
    {
        var header = headers
            .FirstOrDefault(h => h.Key == key);

        return header == null
            ? null
            : System.Text.Encoding.UTF8.GetString(header.GetValueBytes());
    }
}
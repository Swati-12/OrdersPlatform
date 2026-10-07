using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Orders.Api.Services;
using Orders.Api.Shipping;

namespace Orders.Api.Messaging;

public sealed class OrderEventsConsumer(
    IServiceScopeFactory scopes,
    IOptions<KafkaOptions> options,
    ILogger<OrderEventsConsumer> logger)
    : BackgroundService
{
    private readonly KafkaOptions _options = options.Value;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = "orders-shipping",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer =
            new ConsumerBuilder<string, string>(config)
                .Build();

        consumer.Subscribe(
            _options.OrderEventsTopic);

        logger.LogInformation(
            "Shipping Kafka consumer started. GroupId={GroupId}, Topic={Topic}",
            config.GroupId,
            _options.OrderEventsTopic);

        // Allow ASP.NET Core/Kestrel startup to continue
        // before entering the synchronous Kafka consume loop.
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result =
                        consumer.Consume(stoppingToken);

                    await ProcessMessageAsync(
                        result,
                        stoppingToken);

                    consumer.Commit(result);

                    logger.LogInformation(
                        "Kafka message committed. Topic={Topic}, Partition={Partition}, Offset={Offset}",
                        result.Topic,
                        result.Partition.Value,
                        result.Offset.Value);
                }
                catch (ConsumeException ex)
                {
                    logger.LogError(
                        ex,
                        "Kafka consume error");
                }
                catch (Exception ex)
                    when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex,
                        "Error processing Kafka order event");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProcessMessageAsync(
        ConsumeResult<string, string> result,
        CancellationToken ct)
    {
        var eventType =
            result.Message.Headers
                .FirstOrDefault(
                    h => h.Key == "event-type")
                ?.GetValueBytes() is { } bytes
                    ? Encoding.UTF8.GetString(bytes)
                    : null;

        if (!string.Equals(
                eventType,
                "OrderCreated",
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Ignoring Kafka event type {EventType}",
                eventType);

            return;
        }

        var orderEvent =
    JsonSerializer.Deserialize<OrderCreatedEvent>(
        result.Message.Value,
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

if (orderEvent is null ||
    orderEvent.OrderId == Guid.Empty)
{
    logger.LogWarning(
        "Ignoring invalid OrderCreated event. Topic={Topic}, Partition={Partition}, Offset={Offset}",
        result.Topic,
        result.Partition.Value,
        result.Offset.Value);

    return;
}

        using var scope = scopes.CreateScope();

        var shipping =
            scope.ServiceProvider
                .GetRequiredService<IShippingService>();

        await shipping.CreateAsync(
            orderEvent.OrderId,
            ct);

        logger.LogInformation(
            "OrderCreated processed for shipping. OrderId={OrderId}",
            orderEvent.OrderId);
    }

    private sealed record OrderCreatedEvent(
    [property: JsonPropertyName("orderId")] Guid OrderId,
    [property: JsonPropertyName("customerId")] Guid CustomerId,
    [property: JsonPropertyName("total")] decimal Total);
}
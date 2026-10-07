using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Orders.Api.Repositories;

namespace Orders.Api.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(
        Guid eventId,
        string type,
        string payload,
        CancellationToken ct);

    Task PublishDeadLetterAsync(
        Guid eventId,
        string type,
        string payload,
        string reason,
        CancellationToken ct);
}

public sealed class KafkaEventPublisher(
    IProducer<string, string> producer,
    IOptions<KafkaOptions> options,
    ILogger<KafkaEventPublisher> logger) : IEventPublisher
{
    private readonly KafkaOptions _options = options.Value;

    public async Task PublishAsync(
        Guid eventId,
        string type,
        string payload,
        CancellationToken ct)
    {
        var message = new Message<string, string>
        {
            Key = eventId.ToString(),
            Value = payload,
            Headers = new Headers
            {
                { "event-id", System.Text.Encoding.UTF8.GetBytes(eventId.ToString()) },
                { "event-type", System.Text.Encoding.UTF8.GetBytes(type) },
                { "content-type", System.Text.Encoding.UTF8.GetBytes("application/json") }
            }
        };

        try
        {
            var result = await producer.ProduceAsync(
                _options.OrderEventsTopic,
                message,
                ct);

            logger.LogInformation(
                "Kafka event published. EventId={EventId}, Type={Type}, Topic={Topic}, Partition={Partition}, Offset={Offset}",
                eventId,
                type,
                result.Topic,
                result.Partition.Value,
                result.Offset.Value);
        }
        catch (ProduceException<string, string> ex)
        {
            logger.LogError(
                ex,
                "Kafka publish failed. EventId={EventId}, Type={Type}",
                eventId,
                type);

            throw;
        }
    }

    public async Task PublishDeadLetterAsync(
        Guid eventId,
        string type,
        string payload,
        string reason,
        CancellationToken ct)
    {
        var message = new Message<string, string>
        {
            Key = eventId.ToString(),
            Value = payload,
            Headers = new Headers
            {
                { "event-id", System.Text.Encoding.UTF8.GetBytes(eventId.ToString()) },
                { "event-type", System.Text.Encoding.UTF8.GetBytes(type) },
                { "dead-letter-reason", System.Text.Encoding.UTF8.GetBytes(reason) },
                { "original-topic", System.Text.Encoding.UTF8.GetBytes(_options.OrderEventsTopic) },
                { "content-type", System.Text.Encoding.UTF8.GetBytes("application/json") }
            }
        };

        var result = await producer.ProduceAsync(
            _options.DeadLetterTopic,
            message,
            ct);

        logger.LogError(
            "Kafka event moved to DLT. EventId={EventId}, Type={Type}, Topic={Topic}, Partition={Partition}, Offset={Offset}, Reason={Reason}",
            eventId,
            type,
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            reason);
    }
}

/// <summary>
/// Reads transactional outbox records and publishes them to Kafka.
/// At-least-once delivery is guaranteed by the outbox.
/// Consumers must therefore be idempotent.
/// </summary>
public class OutboxPublisher(
    IServiceScopeFactory scopes,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    public const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProcessBatchAsync(stoppingToken);
                }
                catch (Exception ex)
                    when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex,
                        "Outbox batch processing failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutting down.
        }
    }

    public async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();

        var outbox =
            scope.ServiceProvider
                .GetRequiredService<IOutboxRepository>();

        var publisher =
            scope.ServiceProvider
                .GetRequiredService<IEventPublisher>();

        var batch =
            await outbox.GetPendingAsync(20, ct);

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(
                    message.Id,
                    message.Type,
                    message.Payload,
                    ct);

                message.ProcessedAtUtc =
                    DateTime.UtcNow;

                logger.LogInformation(
                    "Outbox message {MessageId} processed",
                    message.Id);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                message.Attempts++;

                message.LastError =
                    ex.Message.Length > 1000
                        ? ex.Message[..1000]
                        : ex.Message;

                logger.LogWarning(
                    ex,
                    "Kafka publish failed for outbox message {MessageId}. Attempt {Attempt}/{MaxAttempts}",
                    message.Id,
                    message.Attempts,
                    MaxAttempts);

                if (message.Attempts >= MaxAttempts)
                {
                    try
                    {
                        await publisher.PublishDeadLetterAsync(
                            message.Id,
                            message.Type,
                            message.Payload,
                            message.LastError,
                            ct);

                        message.DeadLettered = true;

                        logger.LogError(
                            "Outbox message {MessageId} moved to Kafka DLT",
                            message.Id);
                    }
                    catch (Exception dltException)
                        when (dltException is not OperationCanceledException)
                    {
                        // Do NOT mark it dead-lettered if the DLT
                        // itself could not be reached.
                        logger.LogCritical(
                            dltException,
                            "Failed to publish outbox message {MessageId} to Kafka DLT",
                            message.Id);
                    }
                }
            }
        }

        if (batch.Count > 0)
        {
            await outbox.SaveChangesAsync(ct);
        }
    }
}
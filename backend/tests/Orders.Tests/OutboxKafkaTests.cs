using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Orders.Api.Data;
using Orders.Api.Entities;
using Orders.Api.Messaging;
using Orders.Api.Repositories;

namespace Orders.Tests;

[TestFixture]
public class OutboxKafkaTests
{
    private static ServiceProvider BuildProvider(
        IEventPublisher publisher)
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();

        services.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase(dbName));

        services.AddScoped<IOutboxRepository, OutboxRepository>();

        services.AddSingleton(publisher);

        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedMessageAsync(
        ServiceProvider provider)
    {
        using var scope = provider.CreateScope();

        var db =
            scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

        var message =
            OutboxMessage.Create(
                "OrderCreated",
                new
                {
                    orderId = Guid.NewGuid()
                });

        db.OutboxMessages.Add(message);

        await db.SaveChangesAsync();

        return message.Id;
    }

    private static async Task<OutboxMessage> LoadMessageAsync(
        ServiceProvider provider)
    {
        using var scope = provider.CreateScope();

        var db =
            scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .SingleAsync();
    }

    private static OutboxPublisher CreateWorker(
        ServiceProvider provider)
    {
        return new OutboxPublisher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxPublisher>.Instance);
    }

    // ---------------------------------------------------------
    // Successful Kafka publish
    // ---------------------------------------------------------

    [Test]
    public async Task ProcessBatchAsync_KafkaPublishSucceeds_MarksOutboxProcessed()
    {
        var publisher =
            new Mock<IEventPublisher>();

        publisher
            .Setup(p => p.PublishAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var provider =
            BuildProvider(publisher.Object);

        await SeedMessageAsync(provider);

        var worker =
            CreateWorker(provider);

        await worker.ProcessBatchAsync(
            CancellationToken.None);

        var message =
            await LoadMessageAsync(provider);

        Assert.Multiple(() =>
        {
            Assert.That(
                message.ProcessedAtUtc,
                Is.Not.Null);

            Assert.That(
                message.DeadLettered,
                Is.False);

            Assert.That(
                message.Attempts,
                Is.EqualTo(0));
        });

        publisher.Verify(
            p => p.PublishAsync(
                It.IsAny<Guid>(),
                "OrderCreated",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---------------------------------------------------------
    // Kafka failure -> retry
    // ---------------------------------------------------------

    [Test]
    public async Task ProcessBatchAsync_KafkaFails_IncrementsAttempts()
    {
        var publisher =
            new Mock<IEventPublisher>();

        publisher
            .Setup(p => p.PublishAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException(
                    "Kafka broker unavailable"));

        await using var provider =
            BuildProvider(publisher.Object);

        await SeedMessageAsync(provider);

        var worker =
            CreateWorker(provider);

        await worker.ProcessBatchAsync(
            CancellationToken.None);

        var message =
            await LoadMessageAsync(provider);

        Assert.Multiple(() =>
        {
            Assert.That(
                message.Attempts,
                Is.EqualTo(1));

            Assert.That(
                message.ProcessedAtUtc,
                Is.Null);

            Assert.That(
                message.DeadLettered,
                Is.False);

            Assert.That(
                message.LastError,
                Is.EqualTo(
                    "Kafka broker unavailable"));
        });
    }

    // ---------------------------------------------------------
    // 5 failures -> Kafka DLT
    // ---------------------------------------------------------

    [Test]
    public async Task ProcessBatchAsync_AfterFiveFailures_PublishesToDlt()
    {
        var publisher =
            new Mock<IEventPublisher>();

        publisher
            .Setup(p => p.PublishAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException(
                    "Kafka broker unavailable"));

        publisher
            .Setup(p => p.PublishDeadLetterAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var provider =
            BuildProvider(publisher.Object);

        var eventId =
            await SeedMessageAsync(provider);

        var worker =
            CreateWorker(provider);

        for (var i = 0;
             i < OutboxPublisher.MaxAttempts;
             i++)
        {
            await worker.ProcessBatchAsync(
                CancellationToken.None);
        }

        var message =
            await LoadMessageAsync(provider);

        Assert.Multiple(() =>
        {
            Assert.That(
                message.Attempts,
                Is.EqualTo(
                    OutboxPublisher.MaxAttempts));

            Assert.That(
                message.DeadLettered,
                Is.True);

            Assert.That(
                message.ProcessedAtUtc,
                Is.Null);
        });

        publisher.Verify(
            p => p.PublishDeadLetterAsync(
                eventId,
                "OrderCreated",
                It.IsAny<string>(),
                "Kafka broker unavailable",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---------------------------------------------------------
    // DLT failure -> message must NOT be marked dead-lettered
    // ---------------------------------------------------------

    [Test]
    public async Task ProcessBatchAsync_DltPublishFails_DoesNotMarkDeadLettered()
    {
        var publisher =
            new Mock<IEventPublisher>();

        publisher
            .Setup(p => p.PublishAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException(
                    "Kafka broker unavailable"));

        publisher
            .Setup(p => p.PublishDeadLetterAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException(
                    "Kafka DLT unavailable"));

        await using var provider =
            BuildProvider(publisher.Object);

        await SeedMessageAsync(provider);

        var worker =
            CreateWorker(provider);

        for (var i = 0;
             i < OutboxPublisher.MaxAttempts;
             i++)
        {
            await worker.ProcessBatchAsync(
                CancellationToken.None);
        }

        var message =
            await LoadMessageAsync(provider);

        Assert.Multiple(() =>
        {
            Assert.That(
                message.Attempts,
                Is.EqualTo(
                    OutboxPublisher.MaxAttempts));

            Assert.That(
                message.DeadLettered,
                Is.False);

            Assert.That(
                message.ProcessedAtUtc,
                Is.Null);
        });
    }

    // ---------------------------------------------------------
    // Once dead-lettered, it should not be processed again
    // ---------------------------------------------------------

    [Test]
    public async Task ProcessBatchAsync_DeadLetteredMessage_IsNotProcessedAgain()
    {
        var publisher =
            new Mock<IEventPublisher>();

        publisher
            .Setup(p => p.PublishAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException(
                    "Kafka broker unavailable"));

        publisher
            .Setup(p => p.PublishDeadLetterAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var provider =
            BuildProvider(publisher.Object);

        await SeedMessageAsync(provider);

        var worker =
            CreateWorker(provider);

        for (var i = 0;
             i < OutboxPublisher.MaxAttempts;
             i++)
        {
            await worker.ProcessBatchAsync(
                CancellationToken.None);
        }

        var messageAfterDlt =
            await LoadMessageAsync(provider);

        Assert.That(
            messageAfterDlt.DeadLettered,
            Is.True);

        // Process again.
        await worker.ProcessBatchAsync(
            CancellationToken.None);

        var messageAfterRetry =
            await LoadMessageAsync(provider);

        Assert.Multiple(() =>
        {
            Assert.That(
                messageAfterRetry.Attempts,
                Is.EqualTo(
                    OutboxPublisher.MaxAttempts));

            Assert.That(
                messageAfterRetry.DeadLettered,
                Is.True);
        });

        publisher.Verify(
            p => p.PublishDeadLetterAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
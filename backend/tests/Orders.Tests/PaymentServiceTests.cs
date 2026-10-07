using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Orders.Api.Auth;
using Orders.Api.Data;
using Orders.Api.Entities;
using Orders.Api.Payments;
using Orders.Api.Repositories;
using Orders.Api.Services;
using Polly;
using Polly.CircuitBreaker;

namespace Orders.Tests;

[TestFixture]
public class PaymentServiceTests
{
    private AppDbContext _db = null!;
    private FakeGateway _gateway = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _gateway = new FakeGateway();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private sealed class FakeCurrentUser(Guid id, bool isAdmin) : ICurrentUser
    {
        public Guid Id { get; } = id;
        public bool IsAdmin { get; } = isAdmin;
    }

    private sealed class FakeGateway : IPaymentGateway
    {
        public int Calls { get; private set; }
        public Func<PaymentResult> Behaviour { get; set; } = () => new PaymentResult(true, "ref_1", null);

        public async Task<PaymentResult> ChargeAsync(Guid orderId, decimal amount, string currency, CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            return Behaviour();
        }
    }

    private PaymentService CreateService(Guid userId, bool isAdmin = false, ResiliencePipeline? pipeline = null) =>
        new(new OrderRepository(_db), new PaymentRepository(_db), new OutboxRepository(_db), _gateway,
            pipeline ?? ResiliencePipeline.Empty, new FakeCurrentUser(userId, isAdmin),
            NullLogger<PaymentService>.Instance);

    private async Task<Order> SeedOrderAsync(Guid customerId, OrderStatus status = OrderStatus.Pending)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(), CustomerId = customerId, Status = status,
            Total = 50m, IdempotencyKey = Guid.NewGuid().ToString()
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    [Test]
    public async Task PayAsync_PendingOrder_MarksOrderPaidAndWritesOutboxMessage()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user);

        var response = await CreateService(user).PayAsync(order.Id, default);

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo("Succeeded"));
            Assert.That(response.Amount, Is.EqualTo(50m));
            Assert.That(_db.Orders.Single().Status, Is.EqualTo(OrderStatus.Paid));
            Assert.That(_db.OutboxMessages.Single().Type, Is.EqualTo("OrderPaid"));
        });
    }

    [Test]
    public async Task PayAsync_CalledTwice_DoesNotChargeTwice()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user);
        var service = CreateService(user);

        var first = await service.PayAsync(order.Id, default);
        var second = await service.PayAsync(order.Id, default);

        Assert.Multiple(() =>
        {
            Assert.That(_gateway.Calls, Is.EqualTo(1));
            Assert.That(second.PaymentId, Is.EqualTo(first.PaymentId));
        });
    }

    [Test]
    public async Task PayAsync_Declined_ThrowsBusinessRuleExceptionAndRecordsFailure()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user);
        _gateway.Behaviour = () => new PaymentResult(false, null, "Card declined");

        Assert.ThrowsAsync<BusinessRuleException>(() => CreateService(user).PayAsync(order.Id, default));

        Assert.Multiple(() =>
        {
            Assert.That(_db.Payments.Single().Status, Is.EqualTo(PaymentStatus.Failed));
            Assert.That(_db.Orders.Single().Status, Is.EqualTo(OrderStatus.Pending));
            Assert.That(_db.OutboxMessages.Single().Type, Is.EqualTo("PaymentFailed"));
        });
    }

    [Test]
    public async Task PayAsync_GatewayUnreachable_ThrowsServiceUnavailableAndRecordsFailure()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user);
        _gateway.Behaviour = () => throw new HttpRequestException("timeout");

        Assert.ThrowsAsync<ServiceUnavailableException>(() => CreateService(user).PayAsync(order.Id, default));

        Assert.That(_db.Payments.Single().Status, Is.EqualTo(PaymentStatus.Failed));
    }

    [Test]
    public async Task PayAsync_RetryAfterDecline_SucceedsAndCountsAttempts()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user);
        var service = CreateService(user);

        _gateway.Behaviour = () => new PaymentResult(false, null, "Card declined");
        Assert.ThrowsAsync<BusinessRuleException>(() => service.PayAsync(order.Id, default));

        _gateway.Behaviour = () => new PaymentResult(true, "ref_2", null);
        var response = await service.PayAsync(order.Id, default);

        Assert.Multiple(() =>
        {
            Assert.That(response.Status, Is.EqualTo("Succeeded"));
            Assert.That(_db.Payments.Single().Attempts, Is.EqualTo(2));
            Assert.That(_db.Orders.Single().Status, Is.EqualTo(OrderStatus.Paid));
        });
    }

    [Test]
    public async Task PayAsync_OrderOwnedByAnotherCustomer_ThrowsNotFoundException()
    {
        var order = await SeedOrderAsync(Guid.NewGuid());

        Assert.ThrowsAsync<NotFoundException>(() => CreateService(Guid.NewGuid()).PayAsync(order.Id, default));
    }

    [Test]
    public async Task PayAsync_CancelledOrder_ThrowsBusinessRuleException()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user, OrderStatus.Cancelled);

        Assert.ThrowsAsync<BusinessRuleException>(() => CreateService(user).PayAsync(order.Id, default));
    }

    [Test]
    public async Task PayAsync_RepeatedGatewayFailures_OpensCircuitAndStopsCallingGateway()
    {
        var user = Guid.NewGuid();
        var order = await SeedOrderAsync(user);
        _gateway.Behaviour = () => throw new HttpRequestException("down");

        var pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
            })
            .Build();
        var service = CreateService(user, pipeline: pipeline);

        Assert.ThrowsAsync<ServiceUnavailableException>(() => service.PayAsync(order.Id, default)); // failure 1
        Assert.ThrowsAsync<ServiceUnavailableException>(() => service.PayAsync(order.Id, default)); // failure 2 -> circuit opens
        Assert.ThrowsAsync<ServiceUnavailableException>(() => service.PayAsync(order.Id, default)); // rejected without a call

        Assert.That(_gateway.Calls, Is.EqualTo(2));
    }
}
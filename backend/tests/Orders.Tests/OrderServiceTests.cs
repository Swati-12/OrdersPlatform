using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Orders.Api.Auth;
using Orders.Api.Data;
using Orders.Api.Dtos;
using Orders.Api.Entities;
using Orders.Api.Repositories;
using Orders.Api.Services;
using Orders.Api.Validators;

namespace Orders.Tests;

[TestFixture]
public class OrderServiceTests
{
    private static readonly Guid KeyboardId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private AppDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db.Database.EnsureCreated(); // seeds the three products
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private sealed class FakeCurrentUser(Guid id, bool isAdmin) : ICurrentUser
    {
        public Guid Id { get; } = id;
        public bool IsAdmin { get; } = isAdmin;
    }

    private OrderService CreateService(Guid userId, bool isAdmin = false) =>
        new(new OrderRepository(_db), new ProductRepository(_db), new OutboxRepository(_db),
            new FakeCurrentUser(userId, isAdmin),
            new CreateOrderRequestValidator(), new UpdateOrderStatusRequestValidator(),
            NullLogger<OrderService>.Instance);

    private static CreateOrderRequest ValidRequest(int quantity = 2) =>
        new([new OrderItemRequest(KeyboardId, quantity)]);

    // ---------- CreateAsync ----------

    [Test]
    public async Task CreateAsync_ValidRequest_UsesTokenUserAndSnapshotsPrice()
    {
        var userId = Guid.NewGuid();

        var (order, created) = await CreateService(userId).CreateAsync(ValidRequest(2), "key-1", default);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True);
            Assert.That(order.CustomerId, Is.EqualTo(userId));
            Assert.That(order.Total, Is.EqualTo(178.00m));
            Assert.That(order.Status, Is.EqualTo("Pending"));
            Assert.That(order.Items, Has.Count.EqualTo(1));
            Assert.That(order.Items[0].UnitPrice, Is.EqualTo(89.00m));
        });
    }

    [Test]
    public async Task CreateAsync_ValidRequest_DecrementsStock()
    {
        await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(2), "key-1", default);

        var product = await _db.Products.FindAsync(KeyboardId);
        Assert.That(product!.Stock, Is.EqualTo(498));
    }

    [Test]
    public async Task CreateAsync_ValidRequest_WritesOrderCreatedOutboxMessage()
    {
        await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "key-1", default);

        var message = _db.OutboxMessages.Single();
        Assert.Multiple(() =>
        {
            Assert.That(message.Type, Is.EqualTo("OrderCreated"));
            Assert.That(message.ProcessedAtUtc, Is.Null);
        });
    }

    [Test]
    public async Task CreateAsync_SameIdempotencyKey_ReturnsOriginalOrderWithoutDuplicate()
    {
        var service = CreateService(Guid.NewGuid());

        var (first, _) = await service.CreateAsync(ValidRequest(), "same-key", default);
        var (second, created) = await service.CreateAsync(ValidRequest(), "same-key", default);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.False);
            Assert.That(second.Id, Is.EqualTo(first.Id));
            Assert.That(_db.Orders.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreateAsync_SameKeyDifferentUsers_CreatesSeparateOrders()
    {
        var (a, _) = await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "shared-key", default);
        var (b, _) = await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "shared-key", default);

        Assert.That(a.Id, Is.Not.EqualTo(b.Id));
    }

    [Test]
    public async Task CreateAsync_InsufficientStock_ThrowsBusinessRuleException()
    {
        (await _db.Products.FindAsync(KeyboardId))!.Stock = 1;
        await _db.SaveChangesAsync();

        Assert.ThrowsAsync<BusinessRuleException>(() =>
            CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(5), "key-1", default));
    }

    [Test]
    public void CreateAsync_UnknownProduct_ThrowsNotFoundException()
    {
        var request = new CreateOrderRequest([new OrderItemRequest(Guid.NewGuid(), 1)]);

        Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService(Guid.NewGuid()).CreateAsync(request, "key-1", default));
    }

    [Test]
    public void CreateAsync_EmptyItems_ThrowsValidationException()
    {
        var request = new CreateOrderRequest(new List<OrderItemRequest>());

        Assert.ThrowsAsync<ValidationException>(() =>
            CreateService(Guid.NewGuid()).CreateAsync(request, "key-1", default));
    }

    // ---------- GetAsync / ListAsync (authorization rules) ----------

    [Test]
    public async Task GetAsync_OrderOwnedByAnotherCustomer_ThrowsNotFoundException()
    {
        var (order, _) = await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "key-1", default);

        Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService(Guid.NewGuid()).GetAsync(order.Id, default));
    }

    [Test]
    public async Task GetAsync_AdminReadingAnyOrder_ReturnsOrder()
    {
        var (order, _) = await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "key-1", default);

        var fetched = await CreateService(Guid.NewGuid(), isAdmin: true).GetAsync(order.Id, default);

        Assert.That(fetched.Id, Is.EqualTo(order.Id));
    }

    [Test]
    public async Task ListAsync_CustomerSuppliesOtherCustomerId_StillReturnsOnlyOwnOrders()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await CreateService(alice).CreateAsync(ValidRequest(), "a-1", default);
        await CreateService(bob).CreateAsync(ValidRequest(), "b-1", default);

        var result = await CreateService(alice).ListAsync(bob, 1, 20, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items, Has.Count.EqualTo(1));
            Assert.That(result.Items.All(o => o.CustomerId == alice), Is.True);
        });
    }

    [Test]
    public async Task ListAsync_AdminWithoutFilter_ReturnsAllOrders()
    {
        await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "a-1", default);
        await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "b-1", default);

        var result = await CreateService(Guid.NewGuid(), isAdmin: true).ListAsync(null, 1, 20, default);

        Assert.That(result.TotalCount, Is.EqualTo(2));
    }

    [TestCase(0, 1)]
    [TestCase(-5, 1)]
    public async Task ListAsync_InvalidPage_ClampsToFirstPage(int page, int expectedPage)
    {
        var user = Guid.NewGuid();
        await CreateService(user).CreateAsync(ValidRequest(), "k", default);

        var result = await CreateService(user).ListAsync(null, page, 20, default);

        Assert.That(result.Page, Is.EqualTo(expectedPage));
    }

    [TestCase(0, 1)]
    [TestCase(1000, 100)]
    public async Task ListAsync_PageSizeOutOfRange_IsClamped(int pageSize, int expected)
    {
        var user = Guid.NewGuid();
        await CreateService(user).CreateAsync(ValidRequest(), "k", default);

        var result = await CreateService(user).ListAsync(null, 1, pageSize, default);

        Assert.That(result.PageSize, Is.EqualTo(expected));
    }

    // ---------- Status lifecycle ----------

    [Test]
    public async Task UpdateStatusAsync_PendingToPaid_Succeeds()
    {
        var service = CreateService(Guid.NewGuid(), isAdmin: true);
        var (order, _) = await service.CreateAsync(ValidRequest(), "key-1", default);

        var updated = await service.UpdateStatusAsync(order.Id, new UpdateOrderStatusRequest(OrderStatus.Paid), default);

        Assert.That(updated.Status, Is.EqualTo("Paid"));
    }

    [TestCase(OrderStatus.Shipped)]
    [TestCase(OrderStatus.Delivered)]
    public async Task UpdateStatusAsync_SkippingAStep_ThrowsBusinessRuleException(OrderStatus target)
    {
        var service = CreateService(Guid.NewGuid(), isAdmin: true);
        var (order, _) = await service.CreateAsync(ValidRequest(), "key-1", default);

        Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.UpdateStatusAsync(order.Id, new UpdateOrderStatusRequest(target), default));
    }

    [Test]
    public async Task UpdateStatusAsync_CancelledOrder_CannotBeReopened()
    {
        var service = CreateService(Guid.NewGuid(), isAdmin: true);
        var (order, _) = await service.CreateAsync(ValidRequest(), "key-1", default);
        await service.CancelAsync(order.Id, default);

        Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.UpdateStatusAsync(order.Id, new UpdateOrderStatusRequest(OrderStatus.Paid), default));
    }

    [Test]
    public async Task CancelAsync_PendingOrder_RestoresStock()
    {
        var service = CreateService(Guid.NewGuid());
        var (order, _) = await service.CreateAsync(ValidRequest(3), "key-1", default);

        await service.CancelAsync(order.Id, default);

        var product = await _db.Products.FindAsync(KeyboardId);
        Assert.That(product!.Stock, Is.EqualTo(500));
    }

    [Test]
    public async Task CancelAsync_OrderOwnedByAnotherCustomer_ThrowsNotFoundException()
    {
        var (order, _) = await CreateService(Guid.NewGuid()).CreateAsync(ValidRequest(), "key-1", default);

        Assert.ThrowsAsync<NotFoundException>(() =>
            CreateService(Guid.NewGuid()).CancelAsync(order.Id, default));
    }
}
using NUnit.Framework;
using Orders.Api.Dtos;
using Orders.Api.Validators;

namespace Orders.Tests;

[TestFixture]
public class ValidatorTests
{
    private static readonly Guid ProductId = Guid.NewGuid();

    // ---------- RegisterRequestValidator ----------

    [TestCase("alice@example.com", "Str0ngPassword1", true)]
    [TestCase("not-an-email", "Str0ngPassword1", false)]
    [TestCase("", "Str0ngPassword1", false)]
    [TestCase("alice@example.com", "short1A", false)]          // too short
    [TestCase("alice@example.com", "alllowercase123", false)]  // no uppercase
    [TestCase("alice@example.com", "ALLUPPERCASE123", false)]  // no lowercase
    [TestCase("alice@example.com", "NoDigitsHereAtAll", false)]
    public void RegisterRequestValidator_Validate_ReturnsExpectedResult(string email, string password, bool expectedValid)
    {
        var result = new RegisterRequestValidator().Validate(new RegisterRequest(email, password));

        Assert.That(result.IsValid, Is.EqualTo(expectedValid));
    }

    // ---------- LoginRequestValidator ----------

    [TestCase("alice@example.com", "anything", true)]
    [TestCase("", "anything", false)]
    [TestCase("alice@example.com", "", false)]
    public void LoginRequestValidator_Validate_ReturnsExpectedResult(string email, string password, bool expectedValid)
    {
        var result = new LoginRequestValidator().Validate(new LoginRequest(email, password));

        Assert.That(result.IsValid, Is.EqualTo(expectedValid));
    }

    // ---------- CreateOrderRequestValidator ----------

    [TestCase(1, true)]
    [TestCase(100, true)]
    [TestCase(0, false)]
    [TestCase(-1, false)]
    [TestCase(101, false)]
    public void CreateOrderRequestValidator_Quantity_ReturnsExpectedResult(int quantity, bool expectedValid)
    {
        var request = new CreateOrderRequest([new OrderItemRequest(ProductId, quantity)]);

        Assert.That(new CreateOrderRequestValidator().Validate(request).IsValid, Is.EqualTo(expectedValid));
    }

    [Test]
    public void CreateOrderRequestValidator_DuplicateProducts_IsInvalid()
    {
        var request = new CreateOrderRequest([new OrderItemRequest(ProductId, 1), new OrderItemRequest(ProductId, 2)]);

        Assert.That(new CreateOrderRequestValidator().Validate(request).IsValid, Is.False);
    }

    [Test]
    public void CreateOrderRequestValidator_MoreThanFiftyLines_IsInvalid()
    {
        var items = Enumerable.Range(0, 51).Select(_ => new OrderItemRequest(Guid.NewGuid(), 1)).ToList();

        Assert.That(new CreateOrderRequestValidator().Validate(new CreateOrderRequest(items)).IsValid, Is.False);
    }

    [Test]
    public void CreateOrderRequestValidator_EmptyProductId_IsInvalid()
    {
        var request = new CreateOrderRequest([new OrderItemRequest(Guid.Empty, 1)]);

        Assert.That(new CreateOrderRequestValidator().Validate(request).IsValid, Is.False);
    }

    // ---------- UpdateOrderStatusRequestValidator ----------

    [TestCase(0, true)]
    [TestCase(4, true)]
    [TestCase(99, false)]
    public void UpdateOrderStatusRequestValidator_Status_ReturnsExpectedResult(int status, bool expectedValid)
    {
        var request = new UpdateOrderStatusRequest((Orders.Api.Entities.OrderStatus)status);

        Assert.That(new UpdateOrderStatusRequestValidator().Validate(request).IsValid, Is.EqualTo(expectedValid));
    }
}
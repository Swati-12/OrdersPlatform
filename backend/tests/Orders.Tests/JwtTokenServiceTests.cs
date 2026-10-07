using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Orders.Api.Auth;
using Orders.Api.Entities;

namespace Orders.Tests;

[TestFixture]
public class JwtTokenServiceTests
{
    private JwtTokenService _service = null!;
    private User _user = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new JwtTokenService(Options.Create(new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-audience",
            SigningKey = "unit-test-signing-key-at-least-32-characters-long!",
            ExpiryMinutes = 30
        }));
        _user = new User { Id = Guid.NewGuid(), Email = "alice@example.com", Role = Roles.Customer };
    }

    [Test]
    public void CreateToken_ValidUser_ContainsSubjectEmailAndRoleClaims()
    {
        var response = _service.CreateToken(_user);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);

        Assert.Multiple(() =>
        {
            Assert.That(token.Subject, Is.EqualTo(_user.Id.ToString()));
            Assert.That(token.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value, Is.EqualTo("alice@example.com"));
            Assert.That(token.Claims.First(c => c.Type == "role").Value, Is.EqualTo(Roles.Customer));
            Assert.That(token.Issuer, Is.EqualTo("test-issuer"));
            Assert.That(token.Audiences, Does.Contain("test-audience"));
        });
    }

    [Test]
    public void CreateToken_ValidUser_ExpiresAfterConfiguredMinutes()
    {
        var response = _service.CreateToken(_user);

        Assert.That(response.ExpiresAtUtc,
            Is.EqualTo(DateTime.UtcNow.AddMinutes(30)).Within(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void CreateToken_AdminUser_ReturnsAdminRole()
    {
        _user.Role = Roles.Admin;

        var response = _service.CreateToken(_user);

        Assert.That(response.Role, Is.EqualTo(Roles.Admin));
    }

    [Test]
    public void CreateToken_CalledTwice_ProducesDifferentTokens()
    {
        var first = _service.CreateToken(_user).AccessToken;
        var second = _service.CreateToken(_user).AccessToken;

        Assert.That(first, Is.Not.EqualTo(second)); // unique jti per token
    }
}
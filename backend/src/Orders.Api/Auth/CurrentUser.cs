using System.Security.Claims;
using Orders.Api.Entities;
using Orders.Api.Services;

namespace Orders.Api.Auth;

/// <summary>Abstraction over the authenticated principal so services stay testable and HTTP-agnostic.</summary>
public interface ICurrentUser
{
    Guid Id { get; }
    bool IsAdmin { get; }
}

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal =>
        accessor.HttpContext?.User is { Identity.IsAuthenticated: true } p
            ? p
            : throw new AuthenticationFailedException("Not authenticated.");

    public Guid Id => Guid.TryParse(Principal.FindFirstValue("sub"), out var id)
        ? id
        : throw new AuthenticationFailedException("Invalid token.");

    public bool IsAdmin => Principal.IsInRole(Roles.Admin);
}
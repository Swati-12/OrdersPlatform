using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Api.Auth;
using Orders.Api.Dtos;
using Orders.Api.Entities;
using Orders.Api.Repositories;

namespace Orders.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct);
}

public class AuthService(
    IUserRepository users,
    IPasswordHasher<User> hasher,
    IJwtTokenService tokens,
    IValidator<RegisterRequest> registerValidator,
    IValidator<LoginRequest> loginValidator) : IAuthService
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        await registerValidator.ValidateAndThrowAsync(request, ct);
        var email = request.Email.Trim().ToLowerInvariant();

        if (await users.EmailExistsAsync(email, ct))
            throw new ConflictException("An account with this email already exists.");

        // Self-registration always creates a Customer; admins are provisioned out-of-band.
        var user = new User { Id = Guid.NewGuid(), Email = email, Role = Roles.Customer };
        user.PasswordHash = hasher.HashPassword(user, request.Password);

        try
        {
            await users.AddAsync(user, ct);
            await users.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("An account with this email already exists.");
        }

        return tokens.CreateToken(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await loginValidator.ValidateAndThrowAsync(request, ct);
        var email = request.Email.Trim().ToLowerInvariant();

        var user = await users.GetByEmailAsync(email, ct);
        var ok = user is not null &&
                 hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) != PasswordVerificationResult.Failed;

        // Same message for unknown email and wrong password: don't reveal which accounts exist.
        if (!ok) throw new AuthenticationFailedException("Invalid email or password.");

        return tokens.CreateToken(user!);
    }
}
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Orders.Api.Auth;
using Orders.Api.Data;
using Orders.Api.Entities;
using Orders.Api.Messaging;
using Orders.Api.Middleware;
using Orders.Api.Payments;
using Orders.Api.Repositories;
using Orders.Api.Services;
using Confluent.Kafka;
using Orders.Api.Shipping;

var builder = WebApplication.CreateBuilder(args);

// ---------- Database ----------
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default"), npg => npg.EnableRetryOnFailure(3)));

// ---------- JWT authentication & authorization ----------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
          ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep claim names as issued: "sub", "role", "email"
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole(Roles.Admin));

// ---------- Rate limiting ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Strict limit for login/register (brute-force protection), applied with [EnableRateLimiting("auth")]
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));

    // Global limit for every endpoint: token bucket per user (or per IP when anonymous)
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetTokenBucketLimiter(
            ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 100,
                TokensPerPeriod = 50,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// ---------- App services ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<IShippingService, ShippingService>();
builder.Services.AddScoped<IShippingProvider, MockShippingProvider>();

builder.Services.AddHostedService<OrderEventsConsumer>();

// Payments: mock gateway + shared (singleton) retry/circuit-breaker pipeline
builder.Services.AddSingleton<IPaymentGateway, MockPaymentGateway>();
builder.Services.AddSingleton(PaymentResilience.CreatePipeline());

// ---------- Kafka ----------
builder.Services.Configure<KafkaOptions>(
    builder.Configuration.GetSection("Kafka"));

var kafkaOptions =
    builder.Configuration
        .GetSection("Kafka")
        .Get<KafkaOptions>()
    ?? throw new InvalidOperationException(
        "Kafka configuration is missing.");

var kafkaProducerConfig = new ProducerConfig
{
    BootstrapServers = kafkaOptions.BootstrapServers,

    ClientId = kafkaOptions.ClientId,

    // Strong durability.
    Acks = Acks.All,

    // Prevent duplicate writes caused by producer retries.
    EnableIdempotence = true,

    // Batch messages efficiently.
    LingerMs = 5,

    // Compress batches.
    CompressionType = CompressionType.Zstd,

    // Don't block indefinitely.
    MessageTimeoutMs = 30000,

    RequestTimeoutMs = 10000
};

if (!string.IsNullOrWhiteSpace(kafkaOptions.SecurityProtocol) &&
    Enum.TryParse<SecurityProtocol>(
        kafkaOptions.SecurityProtocol,
        true,
        out var securityProtocol))
{
    kafkaProducerConfig.SecurityProtocol =
        securityProtocol;
}

if (!string.IsNullOrWhiteSpace(kafkaOptions.SaslMechanism) &&
    Enum.TryParse<SaslMechanism>(
        kafkaOptions.SaslMechanism,
        true,
        out var saslMechanism))
{
    kafkaProducerConfig.SaslMechanism =
        saslMechanism;

    kafkaProducerConfig.SaslUsername =
        kafkaOptions.SaslUsername;

    kafkaProducerConfig.SaslPassword =
        kafkaOptions.SaslPassword;
}

builder.Services.AddSingleton<
    IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(
        kafkaProducerConfig)
        .Build());

builder.Services.AddSingleton<
    IEventPublisher,
    KafkaEventPublisher>();

builder.Services.AddHostedService<OutboxPublisher>();

builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddResponseCaching();
builder.Services.AddHealthChecks();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer",
        BearerFormat = "JWT", In = ParameterLocation.Header,
        Description = "Paste the accessToken returned by /api/auth/login"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ---------- Pipeline (order matters) ----------
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseResponseCaching();
app.UseAuthentication();
app.UseRateLimiter();   // after authentication so the limiter can partition by user
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.MapControllers();
app.MapHealthChecks("/health");

// Demo convenience: create schema, seed products and an admin user.
// Real deployments: use `dotnet ef migrations add Initial` + `dotnet ef database update`.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

    if (!await db.Users.AnyAsync(u => u.Role == Roles.Admin))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var admin = new User { Id = Guid.NewGuid(), Email = "admin@example.com", Role = Roles.Admin };
        admin.PasswordHash = hasher.HashPassword(admin, builder.Configuration["Seed:AdminPassword"] ?? "Admin#12345");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }
}

app.Run();

public partial class Program;
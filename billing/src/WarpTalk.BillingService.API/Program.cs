using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Threading.RateLimiting;

using WarpTalk.BillingService.API.Middleware;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using WarpTalk.BillingService.API.Swagger;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Services.Interface;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

// =======================================================
// SERILOG
// =======================================================
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// =======================================================
// CONFIG
// =======================================================
var requireAuth = builder.Configuration.GetValue<bool>("Security:RequireAuthentication", false);

// Quick dev convenience: disable auth in Development so Swagger/manual testing works
var isDev = builder.Environment.IsDevelopment();
if (isDev)
{
    requireAuth = false;
}

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

// =======================================================
// CORS
// =======================================================
builder.Services.AddCors(options =>
{
    options.AddPolicy("BillingCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else
        {
            throw new InvalidOperationException("CORS not configured.");
        }
    });
});

// =======================================================
// AUTH
// =======================================================
// Register a minimal authentication stack; in production we enable JwtBearer with validation.
if (requireAuth)
{
    var jwtSecret = builder.Configuration["Jwt:Secret"]!;
    var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
    var jwtAudience = builder.Configuration["Jwt:Audience"]!;

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

    // Ensure defaults are set even if another part of the system overrides AddAuthentication
    builder.Services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
    {
        if (string.IsNullOrEmpty(options.DefaultAuthenticateScheme))
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        if (string.IsNullOrEmpty(options.DefaultChallengeScheme))
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    });

    builder.Services.AddAuthorization();
}
else if (isDev)
{
    // Development-only: register a test auth scheme so AuthorizationMiddleware has a valid handler
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "DevTest";
        options.DefaultChallengeScheme = "DevTest";
    }).AddScheme<AuthenticationSchemeOptions, DevTestAuthHandler>("DevTest", opts => { });

    // Make authorization permissive in dev so you can call admin endpoints from Swagger
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true)
            .Build();
    });
}

// =======================================================
// CONTROLLERS
// =======================================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<BillingSwaggerExamplesOperationFilter>();
});

// =======================================================
// DB CONTEXT
// =======================================================
var useInMemory = builder.Configuration.GetValue<bool>("UseInMemoryDatabase");

builder.Services.AddDbContext<BillingDbContext>(options =>
{
    if (useInMemory)
    {
        options.UseInMemoryDatabase("BillingDb");
    }
    else
    {
        options.UseNpgsql(builder.Configuration.GetConnectionString("BillingDb"));
    }

    options.ConfigureWarnings(w =>
        w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// =======================================================
// REPOSITORIES
// =======================================================

// Billing core
builder.Services.AddScoped<ISubscriptionPlanRepository, SubscriptionPlanRepository>();
builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();

// Credit
builder.Services.AddScoped<ICreditLedgerRepository, CreditLedgerRepository>();

// Transaction / Payment
builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();

// Usage
builder.Services.AddScoped<IUsageEventRepository, UsageEventRepository>();

// Meeting
builder.Services.AddScoped<IMeetingUsageSessionRepository, MeetingUsageSessionRepository>();

// Quota snapshot
builder.Services.AddScoped<IWorkspaceQuotaSnapshotRepository, WorkspaceQuotaSnapshotRepository>();

// Audit
builder.Services.AddScoped<IQuotaAuditLogRepository, QuotaAuditLogRepository>();

// =======================================================
// UNIT OF WORK
// =======================================================
builder.Services.AddScoped<IUnitOfWork>(sp =>
    sp.GetRequiredService<BillingDbContext>());

// =======================================================
// APPLICATION SERVICES (FULL DOMAIN)
// =======================================================

builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<ISubscriptionPlanService, SubscriptionPlanService>();

builder.Services.AddScoped<IQuotaService, QuotaService>();
builder.Services.AddScoped<IWorkspaceQuotaSnapshotService, WorkspaceQuotaSnapshotService>();

builder.Services.AddScoped<ITransactionService, TransactionService>();

builder.Services.AddScoped<IUsageEventService, UsageEventService>();
builder.Services.AddScoped<IMeetingUsageSessionService, MeetingUsageSessionService>();

builder.Services.AddScoped<IWorkspaceOwnershipResolver, WorkspaceOwnershipResolver>();

builder.Services.AddScoped<IPaymentService, PaymentService>();
// =======================================================
// PAYMENT
// =======================================================
var useMockPayOs = builder.Configuration.GetValue<bool>("PayOS:UseMockService");

if (useMockPayOs)
{
    builder.Services.AddScoped<IPayOsService, MockPayOsService>();
}
else
{
    builder.Services.AddHttpClient<IPayOsService, PayOsService>();
}

builder.Services.AddScoped<IPaymentService, PaymentService>();

// =======================================================
// HEALTHCHECK
// =======================================================
builder.Services.AddHealthChecks()
    .AddDbContextCheck<BillingDbContext>();

// =======================================================
// RATE LIMITING (Security: Prevent DoS attacks)
// =======================================================
builder.Services.AddRateLimiter(options =>
{
    // Global policy: IP-based, 100 requests per minute
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));

    // Policy 1: Strict limit for checkout/payment endpoints (5 req/min per IP)
    options.AddPolicy("PaymentEndpoints", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));

    // Policy 2: Moderate limit for quota operations (20 req/min per IP)
    options.AddPolicy("QuotaEndpoints", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));

    // Policy 3: Webhooks - higher limit (50 req/min, no user check needed)
    options.AddPolicy("WebhookEndpoints", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 50,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));

    // Policy 4: Admin endpoints - very high limit (500 req/min)
    options.AddPolicy("AdminEndpoints", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 500,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));

    // Policy 5: Health checks - very high limit (10000 req/min - effectively unlimited)
    options.AddPolicy("HealthEndpoints", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10000,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true
            }));

    // On rejected, return 429 Too Many Requests
    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            message = "Too many requests. Please try again later.",
            retryAfter = context.HttpContext.Response.Headers["Retry-After"]
        });
    };
});

// =======================================================
// BUILD APP
// =======================================================
var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Billing:AutoSeedOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

    await db.Database.EnsureDeletedAsync();
    await db.Database.EnsureCreatedAsync();
}

// Middleware
app.UseMiddleware<SensitiveDataMaskingMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Swagger UI - Enable in Development and non-Production environments
if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "WarpTalk Billing API v1");
        c.RoutePrefix = string.Empty; // Serve at /
        c.DefaultModelsExpandDepth(2);
    });
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseCors("BillingCors");

if (requireAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Security headers
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// Correlation ID
app.Use(async (ctx, next) =>
{
    var correlationId = ctx.Request.Headers["X-Correlation-Id"].ToString();
    if (string.IsNullOrEmpty(correlationId))
        correlationId = ctx.TraceIdentifier;

    ctx.Response.Headers["X-Correlation-Id"] = correlationId;

    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
    {
        await next();
    }
});

// Routes
var routes = app.MapControllers();

if (requireAuth)
{
    routes.RequireAuthorization();
}

// Health
app.MapHealthChecks("/health");
app.MapGet("/", () => "WarpTalk Billing Service Running");

app.Run();

public partial class Program { }

// Development-only test auth handler (returns a test user with admin role)
internal class DevTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DevTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
        : base(options, logger, encoder, clock)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "dev"), new Claim(ClaimTypes.Role, "admin") };
        var identity = new ClaimsIdentity(claims, "DevTest");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "DevTest");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
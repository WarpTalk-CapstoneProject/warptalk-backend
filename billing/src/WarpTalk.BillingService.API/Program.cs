using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Serilog;

using WarpTalk.BillingService.API.Middleware;
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
if (requireAuth)
{
    var jwtSecret = builder.Configuration["Jwt:Secret"]!;
    var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
    var jwtAudience = builder.Configuration["Jwt:Audience"]!;

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

    builder.Services.AddAuthorization();
}

// =======================================================
// CONTROLLERS
// =======================================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
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
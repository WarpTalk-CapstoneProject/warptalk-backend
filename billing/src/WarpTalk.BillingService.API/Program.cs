using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.API.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Linq;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Setup Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

var requireAuthentication = builder.Configuration.GetValue("Security:RequireAuthentication", false);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("BillingCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
            return;
        }

        // Keep local development usable when env config is missing.
        if (builder.Environment.IsDevelopment())
        {
            policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
                  .AllowAnyMethod()
                  .AllowAnyHeader();
            return;
        }

        throw new InvalidOperationException("CORS origins must be configured for non-development environments.");
    });
});

if (requireAuthentication)
{
    var jwtSecret = builder.Configuration["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret is required when authentication is enabled.");

    var jwtIssuer = builder.Configuration["Jwt:Issuer"]
        ?? throw new InvalidOperationException("Jwt:Issuer is required when authentication is enabled.");

    var jwtAudience = builder.Configuration["Jwt:Audience"]
        ?? throw new InvalidOperationException("Jwt:Audience is required when authentication is enabled.");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
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

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .Select(e => new
                {
                    Field = e.Key,
                    Message = e.Value?.Errors.First().ErrorMessage
                });

            return new BadRequestObjectResult(new
            {
                status = 400,
                message = "Validation Error",
                errors = errors
            });
        };
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<BillingDbContext>();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "WarpTalk Billing Service API", Version = "v1" });
    
    var apiXmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var apiXmlPath = Path.Combine(AppContext.BaseDirectory, apiXmlFile);
    c.IncludeXmlComments(apiXmlPath);

    var appXmlFile = "WarpTalk.BillingService.Application.xml";
    var appXmlPath = Path.Combine(AppContext.BaseDirectory, appXmlFile);
    if (File.Exists(appXmlPath))
    {
        c.IncludeXmlComments(appXmlPath);
    }
});

// Add DbContext
builder.Services.AddDbContext<BillingDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("BillingDb"),
        npgsql =>
        {
            npgsql.CommandTimeout(15);
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
        });
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Dependency Injection
builder.Services.AddScoped<IUsageQuotaRepository, UsageQuotaRepository>();
builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<IQuotaAuditLogRepository, QuotaAuditLogRepository>();
builder.Services.AddScoped<ISubscriptionPlanRepository, SubscriptionPlanRepository>();
builder.Services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<BillingDbContext>());
builder.Services.AddScoped<IQuotaService, QuotaService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("BillingCors");

var requireServiceClientCertificate = builder.Configuration.GetValue("Security:RequireServiceClientCertificate", false);
var allowedServiceClientThumbprints = builder.Configuration
    .GetSection("Security:AllowedServiceClientCertificateThumbprints")
    .Get<string[]>()
    ?? Array.Empty<string>();

if (requireServiceClientCertificate)
{
    if (allowedServiceClientThumbprints.Length == 0)
    {
        throw new InvalidOperationException("At least one service client certificate thumbprint is required when mTLS is enabled.");
    }

    var allowedThumbprints = allowedServiceClientThumbprints
        .Select(NormalizeThumbprint)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next();
            return;
        }

        var certificate = await context.Connection.GetClientCertificateAsync();
        if (certificate == null || !allowedThumbprints.Contains(NormalizeThumbprint(certificate.Thumbprint)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Valid service client certificate required.",
                traceId = context.TraceIdentifier
            });
            return;
        }

        await next();
    });
}

if (requireAuthentication)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Security Headers
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
    await next();
});

// Enrich logs with WorkspaceId
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].ToString();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = context.TraceIdentifier;
    }

    var workspaceId = context.Request.Headers["X-Workspace-Id"].ToString();

    context.Response.Headers["X-Correlation-Id"] = correlationId;

    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
    {
        if (!string.IsNullOrEmpty(workspaceId))
        {
            using (Serilog.Context.LogContext.PushProperty("WorkspaceId", workspaceId))
            {
                await next();
            }
        }
        else
        {
            await next();
        }
    }
});

var controllerRoute = app.MapControllers();
if (requireAuthentication)
{
    controllerRoute.RequireAuthorization();
}

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapGet("/", () => "WarpTalk Billing Service is running.");


// Auto-Seeding in Development (opt-in only)
var autoSeedOnStartup = builder.Configuration.GetValue("Billing:AutoSeedOnStartup", false);
if (app.Environment.IsDevelopment() && autoSeedOnStartup)
{
    using var scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

    context.Database.EnsureCreated();

    if (!context.SubscriptionPlans.Any())
    {
        var plans = new List<WarpTalk.BillingService.Domain.Entities.SubscriptionPlan>
        {
            new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = WarpTalk.BillingService.Domain.Enums.PlanType.Free, BaseQuotaMinutes = 30, PriceVnd = 0, MaxParticipants = 5, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = WarpTalk.BillingService.Domain.Enums.PlanType.Pro, BaseQuotaMinutes = 500, PriceVnd = 199000, MaxParticipants = 25, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = WarpTalk.BillingService.Domain.Enums.PlanType.Premium, BaseQuotaMinutes = 1000, PriceVnd = 499000, MaxParticipants = 100, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = WarpTalk.BillingService.Domain.Enums.PlanType.Enterprise, BaseQuotaMinutes = 10000, PriceVnd = 0, MaxParticipants = 1000, CreatedAt = DateTime.UtcNow }
        };
        context.SubscriptionPlans.AddRange(plans);
        context.SaveChanges();
    }

    var testWorkspaceId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    if (!context.UsageQuotas.Any(q => q.WorkspaceId == testWorkspaceId))
    {
        var quota = new WarpTalk.BillingService.Domain.Entities.UsageQuota
        {
            Id = Guid.NewGuid(),
            WorkspaceId = testWorkspaceId,
            PlanId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TotalAllocatedMinutes = 500,
            ConsumedMinutes = 0,
            CycleStartDate = DateTime.UtcNow.AddDays(-1),
            CycleEndDate = DateTime.UtcNow.AddDays(30)
        };

        context.UsageQuotas.Add(quota);
        context.SaveChanges();
    }
}

app.Run();

static string NormalizeThumbprint(string? thumbprint)
{
    return (thumbprint ?? string.Empty).Replace(" ", string.Empty).Replace(":", string.Empty).ToUpperInvariant();
}

public partial class Program { }

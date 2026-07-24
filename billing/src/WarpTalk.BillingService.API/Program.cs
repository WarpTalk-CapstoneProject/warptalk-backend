using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Context;
using WarpTalk.BillingService.API.GrpcServices;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.BillingService.API.Extensions;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "BillingService")
    .CreateLogger();

try
{
    Log.Information("Starting WarpTalk Billing Service...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.WebHost.ConfigureKestrel(options =>
    {
        // HTTP 1.1 for Swagger/REST
        options.ListenAnyIP(5107, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);

        // HTTP/2 for gRPC
        options.ListenAnyIP(50057, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });

    // --- DbContext ---
    builder.Services.AddDbContext<BillingDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("BillingDb"),
            npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                npgsqlOptions.CommandTimeout(30);
            }));

    // --- Repositories ---
    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
    var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp => 
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));

    builder.Services.AddScoped<IRedisBillingStore, WarpTalk.BillingService.Infrastructure.Redis.RedisBillingStore>();
    builder.Services.AddScoped<IBillingMessagePublisher, WarpTalk.BillingService.Infrastructure.Messaging.RedisBillingMessagePublisher>();

    // --- Application Services ---
    builder.Services.AddScoped<ICreditService, CreditService>();
    builder.Services.AddScoped<IRealtimeSessionBillingService, RealtimeSessionBillingService>();
    builder.Services.AddScoped<IPlanService, PlanService>();
    builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
    builder.Services.AddScoped<IPaymentService, PaymentService>();
    builder.Services.AddScoped<IInvoiceService, InvoiceService>();
    builder.Services.AddScoped<IRefundService, RefundService>();
    builder.Services.AddScoped<IUsageService, UsageService>();
    builder.Services.AddScoped<IBillingAnalyticsService, BillingAnalyticsService>();
    builder.Services.AddScoped<IWorkspaceAuthorizationService, WorkspaceAuthorizationService>();
    builder.Services.AddScoped<IBillingRateService, BillingRateService>();
    builder.Services.AddScoped<IIdempotencyService, PersistentIdempotencyService>();
    builder.Services.AddScoped<IPaymentAppService, PaymentAppService>();

    // --- Infrastructure Services ---
    builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();
    builder.Services.AddScoped<IStripeWebhookService, StripeWebhookService>();
    builder.Services.AddTransient<Stripe.SubscriptionService>();
    builder.Services.AddScoped<INotificationClient, WarpTalk.BillingService.Infrastructure.Clients.NotificationClient>();
    builder.Services.AddScoped<IWorkspaceClient, WarpTalk.BillingService.Infrastructure.Clients.WorkspaceClient>();

    Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? "sk_test_placeholder";

    // --- Grpc Clients ---
    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient>(o =>
    {
        var url = builder.Configuration["NotificationServiceGrpcUrl"] ?? "http://localhost:50053";
        o.Address = new Uri(url);
    });

    builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceService.WorkspaceServiceClient>(o =>
    {
        var url = builder.Configuration["GrpcSettings:WorkspaceServiceUrl"] ?? "http://localhost:50056";
        o.Address = new Uri(url);
    });

    builder.Services.AddGrpc();
    builder.Services.AddGrpcReflection();

    // --- JWT Authentication ---
    var jwtSettings = builder.Configuration.GetSection("Jwt");
    var secretKey = Environment.GetEnvironmentVariable("JWT__SecretKey") ?? jwtSettings["SecretKey"];
    if (string.IsNullOrWhiteSpace(secretKey))
        throw new InvalidOperationException("JWT SecretKey is not configured. Set JWT__SecretKey environment variable in production.");

    var issuer = Environment.GetEnvironmentVariable("JWT__Issuer") ?? jwtSettings["Issuer"] ?? "WarpTalk";
    var audience = Environment.GetEnvironmentVariable("JWT__Audience") ?? jwtSettings["Audience"] ?? "WarpTalk.API";
    var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = "email",
                RoleClaimType = "role"
            };

            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "UNAUTHORIZED",
                        message = "Authentication required",
                        timestamp = DateTime.UtcNow
                    });
                },
                OnForbidden = async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "FORBIDDEN",
                        message = "Access denied",
                        timestamp = DateTime.UtcNow
                    });
                }
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("default", policy => policy.RequireAuthenticatedUser());
        options.AddPolicy("BillingAdmin", policy => policy.RequireRole("billing_admin"));
    });

    // --- Cors ---
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "*" };
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowSpecificOrigins", policy =>
        {
            policy
                .WithOrigins(corsOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials()
                .WithExposedHeaders("X-Total-Count", "X-Page-Number", "X-Page-Size");
        });
    });

    builder.Services.AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("BillingDb") ?? "",
            name: "Billing DB",
            tags: new[] { "db", "ready" });

    // --- Background Workers ---
    builder.Services.AddHostedService<SubscriptionExpirationWorker>();
    builder.Services.AddHostedService<StaleReservationWorker>();
    builder.Services.AddHostedService<SessionMonitorWorker>();
    builder.Services.AddHostedService<SubscriptionRenewalWorker>();
    builder.Services.AddHostedService<DailyAuditAggregationWorker>();
    builder.Services.AddHostedService<BillingAggregationWorker>();

    builder.Services.AddCustomApiBehavior();
    
    builder.Services.AddOpenApi();

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("live")
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("ready")
    });

    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString();

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TraceId", context.TraceIdentifier))
        {
            context.Items["CorrelationId"] = correlationId;
            context.Response.Headers["X-Correlation-Id"] = correlationId;

            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogInformation(
                "HTTP {Method} {Path} from {RemoteIP} | User: {User}",
                context.Request.Method,
                context.Request.Path,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                context.User?.Identity?.Name ?? "anonymous");

            await next();

            logger.LogInformation(
                "HTTP {Method} {Path} completed with {StatusCode}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode);
        }
    });

    app.UseExceptionHandler(options =>
    {
        options.Run(async context =>
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
            var ex = exceptionHandlerPathFeature?.Error;
            var correlationId = context.Items["CorrelationId"]?.ToString() ?? "unknown";

            logger.LogError(ex, "Unhandled exception in {Path} | CorrelationId: {CorrelationId}", context.Request.Path, correlationId);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "INTERNAL_SERVER_ERROR",
                message = "An unexpected error occurred",
                correlationId,
                timestamp = DateTime.UtcNow
            });
        });
    });

    app.UseCors("AllowSpecificOrigins");
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapGrpcService<BillingServiceGrpc>();
    
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapGrpcReflectionService();
    }

    using (var scope = app.Services.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Log.Information("Database connection verified");
    }

    Log.Information("WarpTalk Billing Service started successfully on http://localhost:5107");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WarpTalk Billing Service terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    await Log.CloseAndFlushAsync();
}

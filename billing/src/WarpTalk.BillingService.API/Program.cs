using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Context;
using WarpTalk.BillingService.API.GrpcServices;
using WarpTalk.BillingService.API.Filters;
using WarpTalk.BillingService.API.Services;
using WarpTalk.BillingService.API.Swagger;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Application.Configuration;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Messaging;
using WarpTalk.BillingService.Infrastructure.Persistence.Contexts;
using WarpTalk.BillingService.Infrastructure.Redis;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.BillingService.Infrastructure.Clients;
using WarpTalk.BillingService.API.Workers;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
using WarpTalk.Shared.Protos;

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
    builder.Services.AddWarpTalkObservability(
        builder.Configuration,
        builder.Environment,
        "warptalk-billing");

    builder.WebHost.ConfigureKestrel(options =>
    {
        // HTTP 1.1 for Swagger/REST
        options.ListenAnyIP(5107, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);

        // HTTP/2 for gRPC
        options.ListenAnyIP(50057, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
    });

    builder.Services.AddDbContext<BillingDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("BillingDb")
                ?? throw new InvalidOperationException("ConnectionStrings:BillingDb is required."),
            npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                npgsqlOptions.CommandTimeout(30);
            }));
    builder.Services
        .AddOptions<BillingRatesOptions>()
        .Bind(builder.Configuration.GetRequiredSection(BillingRatesOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
    builder.Services.AddBillingAuthorizationDependencies();
    builder.Services.AddWarpTalkMassTransit(builder.Configuration);
    builder.Services.AddScoped<IOutboxEventPublisher, MassTransitOutboxEventPublisher>();
    builder.Services.AddScoped<IOutboxClaimStore, OutboxClaimStore>();
    builder.Services.AddScoped<OutboxDispatcher>();
    builder.Services.AddScoped<OutboxReplayService>();
    builder.Services.AddScoped<IBillingService, WarpTalk.BillingService.Application.Services.BillingService>();
    var redisConnectionString =
        builder.Configuration.GetConnectionString("Redis")
        ?? builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis connection string is not configured.");
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(
            $"{redisConnectionString},abortConnect=false"));
    builder.Services.AddScoped<IRedisBillingStore, RedisBillingStore>();
    builder.Services.AddScoped<IBillingMessagePublisher, RedisBillingMessagePublisher>();
    builder.Services.AddScoped<ICreditService, CreditService>();
    builder.Services.AddScoped<IPlanService, PlanService>();
    builder.Services.AddScoped<ICheckoutPricingService, CheckoutPricingService>();
    builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
    builder.Services.AddScoped<IPaymentAndLedgerService, PaymentAndLedgerService>();
    builder.Services.AddScoped<IInvoiceService, InvoiceService>();
    builder.Services.AddScoped<IRefundService, RefundService>();
    builder.Services.AddScoped<IUsageService, UsageService>();
    builder.Services.AddScoped<IIdempotencyService, PersistentIdempotencyService>();
    builder.Services.AddGrpcClient<WorkspaceService.WorkspaceServiceClient>(options =>
    {
        options.Address = builder.Configuration.GetRequiredServiceUri(
            builder.Environment,
            "GrpcUrls:WorkspaceServiceUrl",
            "http://workspace-service:50056");
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
    builder.Services.AddScoped<IWorkspaceDirectory, WorkspaceDirectoryGrpcClient>();
    builder.Services.AddScoped<BillingGrpcService>();
    builder.Services.AddScoped<IStripeBillingGateway, StripeBillingGateway>();
    builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);
    builder.Services.AddGrpcReflection();
    builder.Services.AddHostedService<BillingOutboxWorker>();

    builder.Services.AddWarpTalkJwtAuthentication(
        builder.Configuration,
        builder.Environment,
        options =>
        {
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            options.TokenValidationParameters.NameClaimType = "email";
            options.TokenValidationParameters.RoleClaimType = "role";

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

    builder.Services.AddWarpTalkServiceHealthChecks<BillingDbContext>(
        "billing-database");

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "WarpTalk Billing API",
            Version = "v1"
        });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Input: Bearer {your JWT token}"
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });

        // Include XML documentation for Swagger
        var xmlFile = $"{typeof(Program).Assembly.GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
            options.IncludeXmlComments(xmlPath);

        // Include custom operation filter for ProducesResponseType attributes
        options.OperationFilter<ProducesResponseTypeOperationFilter>();
    });

    var app = builder.Build();

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "WarpTalk Billing API v1");
        options.RoutePrefix = "swagger";
    });

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.MapWarpTalkServiceHealthChecks();

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
    app.MapGrpcService<BillingGrpcService>();
    
    if (app.Environment.IsDevelopment())
    {
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

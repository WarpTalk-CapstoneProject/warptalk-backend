using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Context;
using WarpTalk.AssistantService.API.Hubs;
using WarpTalk.AssistantService.API.Services;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;
using WarpTalk.AssistantService.Infrastructure.Repositories;
using WarpTalk.AssistantService.Infrastructure.Messaging;
using WarpTalk.Shared.Extensions;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AssistantService")
    .CreateLogger();

try
{
    Log.Information("Starting WarpTalk Assistant Service...");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.AddWarpTalkObservability(
        builder.Configuration,
        builder.Environment,
        "warptalk-assistant");

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(5108, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    });

    builder.Services.AddDbContext<AssistantDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("AssistantDb")
                ?? throw new InvalidOperationException("ConnectionStrings:AssistantDb is required."),
            npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                npgsqlOptions.CommandTimeout(30);
            }));

    builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
    builder.Services.AddScoped<IAssistantConversationService, AssistantConversationService>();
    builder.Services.AddScoped<IAssistantNotifier, AssistantNotifier>();
    builder.Services.AddScoped<IAssistantChatRequestPublisher, RedisAssistantChatRequestPublisher>();

    // The OpenAI tool-calling loop runs in ai_assistant_worker (Python) — this service only
    // publishes the chat request (see IAssistantChatRequestPublisher) and consumes the result
    // stream (see AssistantChatResultConsumerService) to update the DB and relay to SignalR.
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddHostedService<AssistantChatResultConsumerService>();

    builder.Services.AddWarpTalkJwtAuthentication(
        builder.Configuration,
        builder.Environment,
        options =>
        {
            options.TokenValidationParameters.NameClaimType = "email";
            options.TokenValidationParameters.RoleClaimType = "role";

            options.Events = new JwtBearerEvents
            {
                // SignalR can't set an Authorization header on the WebSocket handshake —
                // accept the token via query string for the hub path only.
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/api/v1/assistant/chat-hub"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "UNAUTHORIZED",
                        message = "Authentication required",
                        timestamp = DateTime.UtcNow,
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
                        timestamp = DateTime.UtcNow,
                    });
                },
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("default", policy => policy.RequireAuthenticatedUser());
    });

    builder.Services.AddSignalR();

    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "*" };
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowSpecificOrigins", policy =>
        {
            policy
                .WithOrigins(corsOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        });
    });

    builder.Services.AddWarpTalkServiceHealthChecks<AssistantDbContext>(
        "assistant-database");

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "WarpTalk Assistant API", Version = "v1" });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Input: Bearer {your JWT token}",
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
                },
                Array.Empty<string>()
            },
        });
    });

    var app = builder.Build();

    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "WarpTalk Assistant API v1");
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
            await next();
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
                timestamp = DateTime.UtcNow,
            });
        });
    });

    app.UseCors("AllowSpecificOrigins");
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapHub<AssistantHub>("/api/v1/assistant/chat-hub");

    using (var scope = app.Services.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        Log.Information("Database connection verified");
    }

    Log.Information("WarpTalk Assistant Service started successfully on http://localhost:5108");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WarpTalk Assistant Service terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    await Log.CloseAndFlushAsync();
}

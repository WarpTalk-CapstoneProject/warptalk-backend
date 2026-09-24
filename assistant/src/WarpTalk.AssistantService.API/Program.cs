using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Context;
using WarpTalk.AssistantService.API.Hubs;
using WarpTalk.AssistantService.API.Services;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Services;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.AssistantService.Infrastructure.Persistence;
using WarpTalk.AssistantService.Infrastructure.Repositories;
using WarpTalk.AssistantService.Infrastructure.Clients;
using WarpTalk.AssistantService.Infrastructure.Messaging;
using WarpTalk.AssistantService.Infrastructure.Mcp;
using WarpTalk.AssistantService.Infrastructure.OAuth;
using WarpTalk.AssistantService.Infrastructure.Plugins;
using WarpTalk.AssistantService.Infrastructure.Security;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Coordination;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;
using WarpTalk.Shared.Protos;

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
    builder.Configuration.RequirePublicBaseUrl(builder.Environment, "AppBaseUrl");
    var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
    var dataProtection = builder.Services
        .AddDataProtection()
        .SetApplicationName("WarpTalk.AssistantService");
    if (!string.IsNullOrWhiteSpace(keyRingPath))
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

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
    builder.Services.AddScoped<IPluginInstallationService, PluginInstallationService>();
    // The operator-side lifecycle of a catalog row. Separate from the installation service because
    // it writes the global catalog rather than one user's own rows, and is gated accordingly.
    builder.Services.AddScoped<IPluginCatalogAdminService, PluginCatalogAdminService>();
    builder.Services.AddScoped<IMcpConfirmationTokenService, McpConfirmationTokenService>();
    builder.Services.AddScoped<PluginConnectionService>();
    builder.Services.AddScoped<IPluginConnectionService>(sp => sp.GetRequiredService<PluginConnectionService>());
    // Same instance behind the narrow refresh slice McpToolOrchestrator depends on.
    builder.Services.AddScoped<IPluginTokenRefresher>(sp => sp.GetRequiredService<PluginConnectionService>());
    builder.Services.AddScoped<IMcpToolOrchestrator, McpToolOrchestrator>();
    builder.Services.AddScoped<IWorkspacePluginPolicyClient, WorkspacePluginPolicyGrpcClient>();
    builder.Services.AddScoped<IWorkspaceMembershipClient, WorkspaceMembershipGrpcClient>();
    // WT-646. The single place a workspace's plugin policy is applied - the catalog, install,
    // connect and execute paths all judge through this rather than reading the snapshot themselves,
    // so the null-versus-empty allowlist rule exists once.
    builder.Services.AddScoped<IWorkspacePluginGuard, WorkspacePluginGuard>();
    builder.Services.AddScoped<IPluginToolAuditQueryService, PluginToolAuditQueryService>();
    // The workspace plugin marketplace: which plugins a workspace has, private MCP plugins, and
    // members asking the Owner for more. Notifies through the notification service's gRPC.
    builder.Services.AddScoped<IWorkspacePluginMarketplaceService, WorkspacePluginMarketplaceService>();
    builder.Services.AddScoped<IWorkspaceDirectoryClient, WorkspaceDirectoryGrpcClient>();
    builder.Services.AddScoped<IWorkspacePluginMemberService, WorkspacePluginMemberService>();
    builder.Services.AddScoped<IUserNotificationClient, UserNotificationGrpcClient>();
    // Gateways and OAuth clients are resolved per plugin *kind*, not per plugin key, so a real MCP
    // server needs a catalog row rather than a new class. Google keeps a bespoke pair because it
    // has no official remote MCP server for Drive/Calendar.
    builder.Services.AddScoped<IPluginProviderResolver, PluginProviderResolver>();
    builder.Services.Configure<GoogleWorkspaceApiOptions>(
        builder.Configuration.GetSection("Plugins:GoogleWorkspace:Api"));
    builder.Services.AddHttpClient<GoogleWorkspaceMcpToolGateway>();
    builder.Services.AddKeyedScoped<IMcpToolGateway>(
        PluginConstants.PluginKind.Native,
        (sp, _) => sp.GetRequiredService<GoogleWorkspaceMcpToolGateway>());
    builder.Services.Configure<GoogleWorkspaceOAuthOptions>(
        builder.Configuration.GetSection("Plugins:GoogleWorkspace:OAuth"));
    builder.Services.AddHttpClient<GoogleWorkspaceOAuthClient>();
    builder.Services.AddHttpClient<McpToolGateway>()
        .ConfigureMcpEgress(builder.Environment);
    builder.Services.AddKeyedScoped<IMcpToolGateway>(
        PluginConstants.PluginKind.Mcp,
        (sp, _) => sp.GetRequiredService<McpToolGateway>());
    builder.Services.AddHttpClient<McpOAuthClient>()
        .ConfigureMcpEgress(builder.Environment);
    builder.Services.AddKeyedScoped<IPluginOAuthClient>(
        PluginConstants.PluginKind.Mcp,
        (sp, _) => sp.GetRequiredService<McpOAuthClient>());
    builder.Services.AddKeyedScoped<IPluginOAuthClient>(
        PluginConstants.PluginKind.Native,
        (sp, _) => sp.GetRequiredService<GoogleWorkspaceOAuthClient>());
    builder.Services.AddScoped<IPluginOAuthStateProtector, DataProtectionPluginOAuthStateProtector>();
    builder.Services.AddScoped<IPluginCredentialProtector, DataProtectionPluginCredentialProtector>();

    // MCP client registration ladder (WT-602). Registration order below IS the spec's priority
    // order - MCP Authorization 2026-07-28 requires walking pre-registered, then Client ID
    // Metadata Documents, then Dynamic Client Registration, in that order. Do not reorder these
    // without re-reading that requirement; McpClientRegistrationResolver trusts DI order and
    // performs no ordering of its own.
    builder.Services.Configure<McpClientOptions>(builder.Configuration.GetSection("Plugins:Mcp:Client"));
    builder.Services.AddHttpClient<IMcpAuthorizationServerDiscovery, McpAuthorizationServerDiscovery>()
        .ConfigureMcpEgress(builder.Environment);
    builder.Services.AddScoped<IMcpClientRegistrar, PreregisteredClientRegistrar>();
    builder.Services.AddScoped<IMcpClientRegistrar, CimdClientRegistrar>();
    builder.Services.AddHttpClient<DynamicClientRegistrar>()
        .ConfigureMcpEgress(builder.Environment);
    builder.Services.AddScoped<IMcpClientRegistrar>(
        sp => sp.GetRequiredService<DynamicClientRegistrar>());
    builder.Services.AddScoped<IMcpClientRegistrationResolver, McpClientRegistrationResolver>();
    // Singleton: the signing keys are loaded once from configuration and the ECDsa handles are
    // reused, so a per-request store would re-import PEM material on every call.
    builder.Services.AddSingleton<ConfigurationMcpClientSigningKeyStore>();
    builder.Services.AddSingleton<IMcpClientSigningKeyStore>(
        sp => sp.GetRequiredService<ConfigurationMcpClientSigningKeyStore>());
    builder.Services.AddScoped<IMcpClientMetadataProvider, McpClientMetadataProvider>();
    builder.Services.AddScoped<IMcpClientProvisioner, McpClientProvisioner>();
    builder.Services.AddScoped<IMcpConfirmationTokenProtector, DataProtectionMcpConfirmationTokenProtector>();
    builder.Services.AddScoped<IAssistantNotifier, AssistantNotifier>();
    builder.Services.AddScoped<IAssistantChatRequestPublisher, RedisAssistantChatRequestPublisher>();

    builder.Services.AddGrpcClient<WorkspaceService.WorkspaceServiceClient>(o =>
    {
        o.Address = builder.Configuration.GetRequiredServiceUri(
            builder.Environment,
            "GrpcSettings:WorkspaceServiceUrl",
            "http://localhost:50056");
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

    // The platform audit log lives in the workspace service, and this service has no bus - the same
    // situation, and the same synchronous transport, as auth's and translation-room's admin actions.
    // Same address as the workspace client above: one workspace service, two contracts on it. Every
    // marketplace change an admin makes is recorded through it before it is committed.
    builder.Services.AddGrpcClient<AdminAuditService.AdminAuditServiceClient>(o =>
    {
        o.Address = builder.Configuration.GetRequiredServiceUri(
            builder.Environment,
            "GrpcSettings:WorkspaceServiceUrl",
            "http://localhost:50056");
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
    builder.Services.AddScoped<IAdminAuditRecorder, AdminAuditGrpcClient>();

    // Plugin request notifications (member asks the Owner; the Owner decides). Required outside
    // Development like every other gRPC address: GetRequiredServiceUri throws when it is missing, and
    // warptalk-infrastructure's check-grpc-config-coverage.mjs fails a descriptor that omits it.
    builder.Services.AddGrpcClient<NotificationGrpcService.NotificationGrpcServiceClient>(o =>
    {
        o.Address = builder.Configuration.GetRequiredServiceUri(
            builder.Environment,
            "GrpcSettings:NotificationServiceUrl",
            "http://localhost:50054");
    })
    .AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);

    // The OpenAI tool-calling loop runs in ai_assistant_worker (Python) — this service only
    // publishes the chat request (see IAssistantChatRequestPublisher) and consumes the result
    // stream (see AssistantChatResultConsumerService) to update the DB and relay to SignalR.
    var redisConnectionString = builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");
    // Registered as a factory, not an eagerly-connected instance: the old form ran
    // Connect() here at composition time, so an unreachable Redis threw before logging was
    // even configured. abortConnect=false then keeps the throw from moving to the first
    // resolve — the multiplexer comes back disconnected and reconnects on its own, so
    // conversation history still reads from Postgres while the assistant pipeline is down.
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));
    // One replica reads assistant:chat_results at a time (ordered reply chunks); see
    // AssistantChatResultConsumerService.LeaseResource.
    builder.Services.AddWarpTalkLeaderElection(AssistantChatResultConsumerService.LeaseResource);
    builder.Services.AddHostedService<AssistantChatResultConsumerService>();

    builder.Services.AddWarpTalkJwtAuthentication(
        builder.Configuration,
        builder.Environment,
        options =>
        {
            options.TokenValidationParameters.NameClaimType = "email";
            // Roles arrive under ClaimTypes.Role (the auth service issues them that way), so the
            // default RoleClaimType is the one that matches; forcing the short "role" type left
            // every role check in this service unable to see the caller's roles.

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

    // POST /plugins/catalog writes the global catalog, so it takes the platform system-admin gate
    // shared with auth/billing/notification (role 'admin'), not the workspace 'Admin' role.
    builder.Services.AddWarpTalkSystemAdminAuthorization();

    // Multi-replica: AssistantChatResultConsumerService hands each stream entry to ONE pod
    // (consumer group) and AssistantNotifier sends from there; without the Redis backplane only
    // the clients connected to that pod received the reply chunks.
    var signalR = builder.Services.AddSignalR();
    var backplaneRedis = SignalRBackplaneExtensions.ResolveBackplaneConnectionString(builder.Configuration);
    if (backplaneRedis is not null)
    {
        signalR.AddStackExchangeRedis(backplaneRedis, options =>
        {
            options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("WarpTalk.Assistant");
            options.Configuration.AbortOnConnectFail = false;
        });
    }

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
    // WebSockets only: reached through the gateway's YARP route to the Kubernetes Service, which
    // pins nothing to a pod. See SignalRBackplaneExtensions.UseWebSocketsOnly.
    app.MapHub<AssistantHub>("/api/v1/assistant/chat-hub", SignalRBackplaneExtensions.UseWebSocketsOnly);

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

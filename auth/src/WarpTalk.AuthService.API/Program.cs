using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using FluentValidation.AspNetCore;
using StackExchange.Redis;
using WarpTalk.AuthService.API.Extensions;
using WarpTalk.AuthService.API.GrpcServices;
using WarpTalk.AuthService.API.Validators;
using WarpTalk.AuthService.API.Workers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Interfaces.Security;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Domain.Settings;
using WarpTalk.AuthService.Infrastructure.Clients;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using WarpTalk.AuthService.Infrastructure.Security;
using WarpTalk.AuthService.Infrastructure.Storage;
using WarpTalk.AuthService.Infrastructure.Extensions;
using WarpTalk.AuthService.Infrastructure.Services;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.Grpc;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.RequirePublicBaseUrl(builder.Environment, "AppBaseUrl");
builder.Services.AddWarpTalkObservability(
    builder.Configuration,
    builder.Environment,
    "warptalk-auth");

// Kestrel Ports Configuration
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5101, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(50051, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

// DbContext
builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AuthDb")));
builder.Services.AddWarpTalkServiceHealthChecks<AuthDbContext>("auth-database");

// Configuration Options
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("AuthSettings"));
builder.Services.Configure<PasswordHasherSettings>(builder.Configuration.GetSection("PasswordHasherSettings"));

// Repositories & Unit of Work
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();
builder.Services.AddScoped<IPermissionRepository, PermissionRepository>();
builder.Services.AddScoped<IUserRoleRepository, UserRoleRepository>();
builder.Services.AddScoped<IUserSettingRepository, UserSettingRepository>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IVoiceProfileRepository, VoiceProfileRepository>();
builder.Services.AddScoped<IVoiceConsentRepository, VoiceConsentRepository>();
builder.Services.AddScoped<IVoiceSampleRepository, VoiceSampleRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Application Services & Memory Cache
builder.Services.AddMemoryCache();
var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
if (string.IsNullOrWhiteSpace(redisConnectionString))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException("Redis:ConnectionString is required in Production.");
    }

    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddScoped<IVoiceCatalogDirectory, EmptyVoiceCatalogDirectory>();
    // No Redis, so no way to hand a recording to the AI side — see NullVoiceCloneRequestQueue.
    builder.Services.AddScoped<IVoiceCloneRequestQueue, NullVoiceCloneRequestQueue>();
    // Nor any way to hear that a meeting cloned somebody, or to ask for a voice to be destroyed.
    builder.Services.AddSingleton<IVoiceCarryOverQueue, NullVoiceCarryOverQueue>();
    builder.Services.AddScoped<IVoicePreviewQueue, NullVoicePreviewQueue>();
}
else
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString + ",abortConnect=false";
    });

    // Separate from the IDistributedCache above on purpose: the voice catalog is a plain
    // string key written by the Python TTS worker, and IDistributedCache can only read
    // values it wrote itself (it wraps them in its own hash envelope).
    // abortConnect=false: authentication is the one dependency every other service has, and
    // Redis here backs only a cache and the voice catalog. Refusing to boot would take login
    // and token issuance down over a degraded convenience. RedisVoiceCatalogDirectory already
    // fails soft (returns an empty catalog, same as a cold cache).
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(redisConnectionString + ",abortConnect=false"));
    builder.Services.AddScoped<IVoiceCatalogDirectory, RedisVoiceCatalogDirectory>();
    builder.Services.AddScoped<IVoiceCloneRequestQueue, RedisVoiceCloneRequestQueue>();
    // Singleton, unlike its neighbours: it owns the consumer group, and a scoped instance would
    // re-run the XGROUP check on every request instead of once per process.
    builder.Services.AddSingleton<IVoiceCarryOverQueue, RedisVoiceCarryOverQueue>();
    builder.Services.AddHostedService<VoiceCarryOverConsumerWorker>();
    builder.Services.AddScoped<IVoicePreviewQueue, RedisVoicePreviewQueue>();
}
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddResendClient(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IAuthEmailSender, ResendAuthEmailSender>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<IAdminUserService, AdminUserService>();
builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
builder.Services.AddScoped<IUserDirectoryService, UserDirectoryService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<IVoiceProfileService, VoiceProfileService>();
builder.Services.AddScoped<IVoiceConsentService, VoiceConsentService>();
builder.Services.AddScoped<IVoiceCarryOverService, VoiceCarryOverService>();

// Infrastructure Security & Storage Services
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
// Named client so the Google verification calls are poolable and, more importantly, so a test
// can substitute the handler and prove a foreign-client token is rejected.
builder.Services.AddHttpClient(nameof(GoogleTokenVerifier));
builder.Services.AddScoped<IGoogleTokenVerifier, GoogleTokenVerifier>();
builder.Services.AddVoiceSampleStorage(builder.Configuration, builder.Environment);

// Inter-Service gRPC Clients
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceInvitationService.WorkspaceInvitationServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcSettings:WorkspaceServiceUrl",
        "http://localhost:50056");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IWorkspaceInvitationClient, WorkspaceInvitationGrpcClient>();

// The platform audit log lives in the workspace service, and auth has no bus to publish onto.
// Same address as the invitation client above — one workspace service, two contracts on it.
//
// Synchronous by design: AdminUserService records an action before committing it and abandons the
// change when the record fails, which is the only ordering under which "every privileged action
// on an account is audited" is a guarantee rather than a hope.
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.AdminAuditService.AdminAuditServiceClient>(o =>
{
    o.Address = builder.Configuration.GetRequiredServiceUri(
        builder.Environment,
        "GrpcSettings:WorkspaceServiceUrl",
        "http://localhost:50056");
})
.AddWarpTalkGrpcClientDefaults(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IAdminAuditRecorder, AdminAuditGrpcClient>();

// Clean & Secure JWT Authentication
builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
// The gate every ~/api/v1/admin/* endpoint shares. Auth is the last service to need it, and
// AdminUsersController is why: without this registration the policy name resolves to nothing and
// the attribute throws at request time instead of refusing the caller.
builder.Services.AddWarpTalkSystemAdminAuthorization();

// Validation & Custom API Behavior
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddCustomApiBehavior();

builder.Services.AddControllers();
builder.Services.AddWarpTalkGrpcServer(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<UserServiceGrpc>();
app.MapWarpTalkServiceHealthChecks();

app.Run();

public partial class Program { }

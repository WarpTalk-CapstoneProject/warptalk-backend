using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FluentValidation;
using FluentValidation.AspNetCore;
using WarpTalk.AuthService.API.Extensions;
using WarpTalk.AuthService.API.GrpcServices;
using WarpTalk.AuthService.API.Validators;
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
using WarpTalk.Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Kestrel Ports Configuration
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5101, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(50051, listenOptions => listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

// DbContext
builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AuthDb")));

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
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// Application Services & Memory Cache
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<IUserSettingsService, UserSettingsService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<IVoiceProfileService, VoiceProfileService>();

// Infrastructure Security & Storage Services
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IGoogleTokenVerifier, GoogleTokenVerifier>();
builder.Services.AddSingleton<IVoiceSampleStorage, LocalVoiceSampleStorage>();

// Inter-Service gRPC Clients
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.WorkspaceInvitationService.WorkspaceInvitationServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:WorkspaceServiceUrl"] ?? "http://localhost:50056");
});
builder.Services.AddScoped<IWorkspaceInvitationClient, WorkspaceInvitationGrpcClient>();

// Clean & Secure JWT Authentication
builder.Services.AddWarpTalkJwtAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

// Validation & Custom API Behavior
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddCustomApiBehavior();

builder.Services.AddControllers();
builder.Services.AddGrpc();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<UserServiceGrpc>();

app.Run();

public partial class Program { }

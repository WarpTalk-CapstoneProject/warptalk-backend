using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using WarpTalk.AuthService.Infrastructure.Security;
using WarpTalk.AuthService.API.GrpcServices;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5101, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });
    options.ListenAnyIP(50051, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

// --- DbContext ---
builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AuthDb")));

// --- Repositories ---
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// --- Application Services ---
builder.Services.AddScoped<IAuthService, WarpTalk.AuthService.Application.Services.AuthService>();

// --- Infrastructure Services ---
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();

// --- JWT Authentication ---
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddGrpc();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<UserServiceGrpc>();

app.Run();

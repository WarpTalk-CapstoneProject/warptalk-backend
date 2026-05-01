using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;
using WarpTalk.NotificationService.Infrastructure.Repositories;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services;
using WarpTalk.NotificationService.API.GrpcServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5104, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50054, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<INotificationService, NotificationService>();

var rawJwtSecret = builder.Configuration["Jwt:Secret"];
var isDefaultOrInvalid = string.IsNullOrWhiteSpace(rawJwtSecret) || 
                         rawJwtSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                         rawJwtSecret.Length < 32;

if (builder.Environment.IsProduction() && isDefaultOrInvalid)
{
    throw new InvalidOperationException("CRITICAL SECURITY: JWT Secret is not properly configured for Production. It must be at least 32 characters long and not be the default placeholder.");
}

// In non-production, fallback to default if invalid
var validatedSecret = isDefaultOrInvalid 
    ? "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!" 
    : rawJwtSecret;

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(validatedSecret!))
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddGrpc(options => 
{
    options.Interceptors.Add<WarpTalk.NotificationService.API.Interceptors.InternalAuthInterceptor>();
});

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.GatewayRealtimeService.GatewayRealtimeServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["Gateway:Address"] ?? "http://localhost:5100");
})
.AddCallCredentials((context, metadata, serviceProvider) =>
{
    var config = serviceProvider.GetRequiredService<IConfiguration>();
    var env = serviceProvider.GetRequiredService<IWebHostEnvironment>();
    
    var rawGrpcSecret = config["Grpc:InternalSecret"];
    var isDefaultOrInvalid = string.IsNullOrWhiteSpace(rawGrpcSecret) || 
                             rawGrpcSecret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                             rawGrpcSecret.Length < 32;

    if (env.IsProduction() && isDefaultOrInvalid)
    {
        throw new InvalidOperationException("CRITICAL SECURITY: Grpc Internal Secret is not properly configured for Production. It must be at least 32 characters long and not be the default placeholder.");
    }

    var secret = isDefaultOrInvalid 
        ? "CHANGE_ME_INTERNAL_SECRET_MIN_32_CHARS_LONG!!" 
        : rawGrpcSecret!;
        
    metadata.Add("x-internal-token", secret);
    return Task.CompletedTask;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<NotificationGrpcServiceImpl>();

app.Run();


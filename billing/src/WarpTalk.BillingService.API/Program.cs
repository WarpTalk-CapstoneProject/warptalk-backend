using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WarpTalk.BillingService.Application.Interfaces;
using System.Threading.RateLimiting;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence.Contexts;
using WarpTalk.BillingService.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1 — REST API (via Gateway)
    options.ListenAnyIP(5107, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2 — gRPC (internal)
    options.ListenAnyIP(50057, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});


builder.Services.AddDbContext<BillingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("BillingDb")));

// --- Repositories ---
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

// --- Application Services ---
builder.Services.AddScoped<ISubscriptionManagementService, SubscriptionManagementService>();
builder.Services.AddScoped<ICreditAndUsageService, CreditAndUsageService>();
builder.Services.AddScoped<IPaymentAndLedgerService, PaymentAndLedgerService>();
builder.Services.AddScoped<IRedisBillingStore, WarpTalk.BillingService.Infrastructure.Redis.RedisBillingStore>();
// --- Background Workers ---
builder.Services.AddHostedService<WarpTalk.BillingService.API.Workers.SessionMonitorWorker>();
builder.Services.AddHostedService<WarpTalk.BillingService.API.Workers.SubscriptionExpirationWorker>();
builder.Services.AddHostedService<WarpTalk.BillingService.API.Workers.StaleReservationWorker>();
// --- Messaging (Redis) ---
var redisConnString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnString));
builder.Services.AddSingleton<IBillingMessagePublisher, WarpTalk.BillingService.Infrastructure.Messaging.RedisBillingMessagePublisher>();

// Note: BillingGrpcService no longer injects IUnitOfWork directly.
// All persistence access is delegated through Application services.

// --- Authentication ---
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
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"]
                    ?? "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!"))
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// --- Rate Limiting ---
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? httpContext.Request.Headers.Host.ToString(),
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.PaymentService.PaymentServiceClient>(o =>
{
    var url = builder.Configuration["PaymentServiceGrpcUrl"] ?? "http://localhost:50058"; // Adjust port if needed
    o.Address = new Uri(url);
});

builder.Services.AddOpenApi();
builder.Services.AddGrpc();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<WarpTalk.BillingService.API.GrpcServices.BillingGrpcService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();

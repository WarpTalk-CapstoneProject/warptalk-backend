using System.Net;
using Microsoft.EntityFrameworkCore;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Persistence;
using WarpTalk.TranscriptService.Infrastructure.Repositories;
using WarpTalk.TranscriptService.Application.Interfaces;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.API.GrpcServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5103, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50053, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

builder.Services.AddDbContext<TranscriptDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("TranscriptDb")));

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<ITranscriptService, WarpTalk.TranscriptService.Application.Services.TranscriptService>();

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"] ?? "REDACTED_JWT_SECRET"))
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddGrpc();
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:AuthServiceUrl"]!);
});

builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.MeetingService.MeetingServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcUrls:MeetingServiceUrl"]!);
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<TranscriptGrpcService>();

app.Run();

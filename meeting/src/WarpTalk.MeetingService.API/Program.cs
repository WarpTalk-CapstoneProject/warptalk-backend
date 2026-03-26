using Microsoft.EntityFrameworkCore;
using WarpTalk.MeetingService.API.GrpcServices;
using WarpTalk.MeetingService.Domain.Interfaces;
using WarpTalk.MeetingService.Infrastructure.Persistence;
using WarpTalk.MeetingService.Infrastructure.Repositories;
using WarpTalk.Shared.Protos;
using WarpTalk.MeetingService.Application.Interfaces;
using WarpTalk.MeetingService.Application.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP/1-only port for REST API Gateway
    options.ListenAnyIP(5102, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // HTTP/2-only port for gRPC
    options.ListenAnyIP(50052, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

builder.Services.AddDbContext<MeetingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("MeetingDb")));

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<IMeetingService, WarpTalk.MeetingService.Application.Services.MeetingService>();

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
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:AuthServiceUrl"] ?? "http://localhost:5101");
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddGrpc();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<MeetingGrpcService>();

app.Run();

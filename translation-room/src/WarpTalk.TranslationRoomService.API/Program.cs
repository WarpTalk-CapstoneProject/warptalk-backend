using Npgsql;
using Npgsql.NameTranslation;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.API.GrpcServices;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using WarpTalk.Shared.Protos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using FluentValidation;
using FluentValidation.AspNetCore;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.API.Extensions;
using WarpTalk.TranslationRoomService.API.Validators;
using TranslationRoomAppService = WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService;
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

var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("TranslationRoomDb"));
var dataSource = dataSourceBuilder.Build();


builder.Services.AddDbContext<TranslationRoomDbContext>(options =>
{
    options.UseNpgsql(dataSource);
    options.EnableSensitiveDataLogging();
    options.EnableDetailedErrors();
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
builder.Services.AddScoped<ITranslationRoomRepository, TranslationRoomRepository>();
builder.Services.AddScoped<ITranslationRoomParticipantRepository, TranslationRoomParticipantRepository>();
builder.Services.AddScoped<ITranslationRoomService, TranslationRoomAppService>();
builder.Services.AddScoped<ITranslationRoomParticipantService, WarpTalk.TranslationRoomService.Application.Services.TranslationRoomParticipantService>();
builder.Services.AddScoped<ILanguageRepository, LanguageRepository>();
builder.Services.AddScoped<WarpTalk.TranslationRoomService.Application.LanguagePolicy.ILanguagePolicy, WarpTalk.TranslationRoomService.Application.LanguagePolicy.LanguagePolicy>();
builder.Services.AddScoped<IUserSettingsRepository, UserSettingsRepository>();

// Register FluentValidation Validators
builder.Services.AddValidatorsFromAssemblyContaining<CreateTranslationRoomRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Secret"] ?? "CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!"))
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddGrpcClient<UserService.UserServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcSettings:AuthServiceUrl"] ?? "http://localhost:5101");
});

builder.Services.AddControllers();
builder.Services.AddCustomApiBehavior();
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
app.MapGrpcService<TranslationRoomGrpcService>();

app.Run();
//for integration test only
public partial class Program { }

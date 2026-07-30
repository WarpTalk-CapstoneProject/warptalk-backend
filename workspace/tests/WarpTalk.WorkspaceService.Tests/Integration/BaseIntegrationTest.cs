using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Testcontainers.PostgreSql;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using Xunit;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace WarpTalk.WorkspaceService.Tests.Integration;

public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    protected HttpClient Client { get; private set; } = null!;
    protected IAuthIdentityClient MockAuthIdentity { get; private set; } = null!;
    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        MockAuthIdentity = Substitute.For<IAuthIdentityClient>();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Do not use the Windows EventLog provider in the disposable
                    // test host; MassTransit's shutdown can log after that provider
                    // has already been disposed on Windows.
                    services.AddLogging(logging => logging.ClearProviders());

                    // Swap DbContext to use Testcontainer Postgres
                    var dbDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<WorkspaceDbContext>));
                    if (dbDescriptor != null) services.Remove(dbDescriptor);

                    services.AddDbContext<WorkspaceDbContext>(options =>
                        options.UseNpgsql(_dbContainer.GetConnectionString()));

                    // Swap distributed cache (Redis) with memory cache
                    var cacheDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(IDistributedCache));
                    if (cacheDescriptor != null) services.Remove(cacheDescriptor);

                    services.AddDistributedMemoryCache();

                    // Swap ConnectionMultiplexer with mock to prevent connection attempts
                    var redisMultiplexerDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(StackExchange.Redis.IConnectionMultiplexer));
                    if (redisMultiplexerDescriptor != null) services.Remove(redisMultiplexerDescriptor);

                    var mockMultiplexer = Substitute.For<StackExchange.Redis.IConnectionMultiplexer>();
                    var mockDatabase = Substitute.For<StackExchange.Redis.IDatabase>();
                    mockMultiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(mockDatabase);
                    services.AddSingleton(mockMultiplexer);

                    // Swap AuthIdentity client with Mock
                    var authClientDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(IAuthIdentityClient));
                    if (authClientDescriptor != null) services.Remove(authClientDescriptor);

                    services.AddScoped(_ => MockAuthIdentity);
                });
            });

        Client = Factory.CreateClient();

        // Run migrations/create tables
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS workspace CASCADE;");
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS workspace;");
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS auth;");

        var createUuidV7Sql = @"
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
            DECLARE
                timestamp_ms bigint;
                timestamp_hex text;
                uuid_hex text;
            BEGIN
                timestamp_ms := (extract(epoch from clock_timestamp()) * 1000)::bigint;
                timestamp_hex := lpad(to_hex(timestamp_ms), 12, '0');
                uuid_hex := timestamp_hex || '7' || lpad(to_hex((random() * 4095)::integer), 3, '0') || '8' || lpad(to_hex((random() * 4095)::integer), 3, '0') || lpad(to_hex((random() * 281474976710655)::bigint), 12, '0');
                RETURN uuid_hex::uuid;
            END;
            $$ LANGUAGE plpgsql;
        ";
        await db.Database.ExecuteSqlRawAsync(createUuidV7Sql);
        await db.Database.EnsureCreatedAsync();
    }

    protected string GenerateJwtToken(Guid userId, string email)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes("CHANGE_ME_SUPER_SECRET_KEY_MIN_32_CHARS_LONG!!");
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email)
            }),
            Issuer = "WarpTalk.AuthService",
            Audience = "WarpTalk",
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        Factory.Dispose();
    }
}

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Tests.Integration;

public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    protected HttpClient Client { get; private set; } = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Remove existing DbContext registration
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<TranslationRoomDbContext>));
                    if (descriptor != null) services.Remove(descriptor);

                    // Add test DbContext with Testcontainer connection string
                    services.AddDbContext<TranslationRoomDbContext>(options =>
                        options.UseNpgsql(_dbContainer.GetConnectionString()));

                    // Add Test Auth
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options => { });
                    
                    services.AddAuthorization(options =>
                    {
                        options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                            .RequireAuthenticatedUser()
                            .Build();
                    });
                });
            });

        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        // Run Migrations
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranslationRoomDbContext>();
        db.Database.ExecuteSqlRaw("CREATE OR REPLACE FUNCTION public.uuid_generate_v7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await db.Database.EnsureCreatedAsync();

        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS translation_room;");
        db.Database.ExecuteSqlRaw("CREATE SCHEMA IF NOT EXISTS platform;");
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS platform.supported_languages (code CHAR(5) PRIMARY KEY, name VARCHAR(100) NOT NULL, is_active BOOLEAN DEFAULT TRUE);");
        db.Database.ExecuteSqlRaw("INSERT INTO platform.supported_languages (code, name) VALUES ('en', 'English'), ('vi', 'Vietnamese'), ('fr', 'French'), ('es', 'Spanish') ON CONFLICT DO NOTHING;");
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        _factory.Dispose();
    }
}

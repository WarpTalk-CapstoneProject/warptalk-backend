using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.AuthService.Tests.Integration;

public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    protected HttpClient Client { get; private set; } = null!;
    protected IWorkspaceInvitationClient MockWorkspaceInvitationClient { get; private set; } = null!;
    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        MockWorkspaceInvitationClient = Substitute.For<IWorkspaceInvitationClient>();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Swap DbContext to use Testcontainer Postgres
                    var dbDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AuthDbContext>));
                    if (dbDescriptor != null) services.Remove(dbDescriptor);

                    services.AddDbContext<AuthDbContext>(options =>
                        options.UseNpgsql(_dbContainer.GetConnectionString()));

                    // Swap WorkspaceInvitationClient client with Mock
                    var clientDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(IWorkspaceInvitationClient));
                    if (clientDescriptor != null) services.Remove(clientDescriptor);

                    services.AddScoped(_ => MockWorkspaceInvitationClient);
                });
            });

        Client = Factory.CreateClient();

        // Run migrations/create tables
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS auth CASCADE;");

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

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        Factory.Dispose();
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.BillingService.Tests.Integration;

public abstract class BaseIntegrationTest : IAsyncLifetime
{
    private const string GrpcInternalSecretEnvironmentVariable = "Grpc__InternalSecret";
    private const string TestGrpcInternalSecret = "test-grpc-internal-token-000000000000";

    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    protected HttpClient Client { get; private set; } = null!;
    protected WebApplicationFactory<Program> Factory { get; private set; } = null!;
    protected IServiceProvider ServiceProvider => Factory.Services;

    public virtual async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        Environment.SetEnvironmentVariable(GrpcInternalSecretEnvironmentVariable, TestGrpcInternalSecret);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BillingDbContext>));
                    if (dbDescriptor != null) services.Remove(dbDescriptor);

                    services.AddDbContext<BillingDbContext>(options =>
                        options.UseNpgsql(_dbContainer.GetConnectionString()));
                });
            });

        Client = Factory.CreateClient();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS subscription CASCADE;");
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS subscription;");

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
        await ExecuteSqlTextAsync(db, createUuidV7Sql);
        await db.Database.EnsureCreatedAsync();

        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations"));
        if (Directory.Exists(basePath))
        {
            var migrationsToApply = new[] 
            { 
                "004-add-atomic-usage-settlement-functions.sql",
                "007-seed-enterprise-subscription-plan.sql",
                "008-phase3-contract-overage-settlement.sql",
                "017-add-just-entered-overage-to-settlement.sql"
            };

            foreach (var migration in migrationsToApply)
            {
                var filePath = Path.Combine(basePath, migration);
                if (File.Exists(filePath))
                {
                    var sql = await File.ReadAllTextAsync(filePath);
                    await ExecuteSqlTextAsync(db, sql);
                }
            }
        }
    }

    public virtual async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        Factory.Dispose();
        Environment.SetEnvironmentVariable(GrpcInternalSecretEnvironmentVariable, null);
    }

    private static async Task ExecuteSqlTextAsync(BillingDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

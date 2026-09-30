using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Infrastructure.Persistence;
using WarpTalk.WorkspaceService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.WorkspaceService.Tests.PlatformSettings;

/// <summary>
/// The platform settings tables against real PostgreSQL, built by the REAL migration file (not by
/// EnsureCreated): every HasColumnName matches a column the migration creates, the latest-change
/// query runs, the version column refuses a lost update, and the scope constraints hold.
///
/// Set WARPTALK_TEST_POSTGRES to a server you already run to skip Testcontainers.
/// </summary>
public sealed class PlatformSettingsDatabaseTests : IAsyncLifetime
{
    private const string Migration = "20260925210000_add_platform_settings_registry.sql";

    private PostgreSqlContainer? _container;
    private string _adminConnection = null!;
    private string _database = null!;
    private string _connection = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("WARPTALK_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(external))
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _container.StartAsync();
            _adminConnection = _container.GetConnectionString();
        }
        else
        {
            _adminConnection = external;
        }

        _database = "settings_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        _connection = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _database }.ConnectionString;
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS workspace;");
        await context.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$ SELECT gen_random_uuid() $$ LANGUAGE sql;");
        await context.Database.ExecuteSqlRawAsync(
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'warptalk_workspace_runtime') THEN CREATE ROLE warptalk_workspace_runtime; END IF; END $$;");
        await context.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(FindMigration()));
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        if (_container is not null) await _container.DisposeAsync();
    }

    [Fact]
    public async Task Values_and_history_round_trip_through_the_migrated_tables()
    {
        var actor = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var context = Context())
        {
            var values = new PlatformSettingValueRepository(context);
            var changes = new PlatformSettingChangeRepository(context);
            await values.AddAsync(Value("limits.document_upload_mb", "platform", "", "20", actor, now));
            await values.AddAsync(Value("limits.document_upload_mb", "plan", "pro", "50", actor, now));
            await changes.AppendAsync(Change("limits.document_upload_mb", null, "20", actor, now.AddSeconds(-5)));
            await changes.AppendAsync(Change("limits.document_upload_mb", "20", "25", actor, now));
            await changes.AppendAsync(Change("meetings.chunk_duration_ms", null, "5000", actor, now.AddSeconds(-1)));
            await context.SaveChangesAsync();
        }

        await using (var context = Context())
        {
            var values = await new PlatformSettingValueRepository(context).GetAllAsync();
            Assert.Equal(2, values.Count);
            Assert.Equal("50", values.Single(v => v.ScopeType == "plan").ValueJson);

            var changes = new PlatformSettingChangeRepository(context);
            var latest = await changes.GetLatestPerKeyAsync();
            Assert.Equal("25", latest["limits.document_upload_mb"].NewValueJson);
            Assert.Equal("5000", latest["meetings.chunk_duration_ms"].NewValueJson);

            var recent = await changes.GetRecentAsync("limits.document_upload_mb", 10);
            Assert.Equal(new[] { "25", "20" }, recent.Select(c => c.NewValueJson));
        }
    }

    [Fact]
    public async Task Two_editors_saving_the_same_version_cannot_both_win()
    {
        await using (var seed = Context())
        {
            await new PlatformSettingValueRepository(seed).AddAsync(Value("meetings.chunk_duration_ms", "platform", "", "5000", Guid.NewGuid(), DateTime.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using var first = Context();
        await using var second = Context();
        var a = await new PlatformSettingValueRepository(first).GetAsync("meetings.chunk_duration_ms", "platform", "");
        var b = await new PlatformSettingValueRepository(second).GetAsync("meetings.chunk_duration_ms", "platform", "");
        a!.ValueJson = "4000";
        a.Version += 1;
        b!.ValueJson = "3000";
        b.Version += 1;

        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task The_scope_constraints_refuse_a_platform_row_with_a_scope_id()
    {
        await using var context = Context();
        await new PlatformSettingValueRepository(context).AddAsync(Value("meetings.chunk_duration_ms", "platform", "oops", "5000", Guid.NewGuid(), DateTime.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private WorkspaceDbContext Context()
        => new(new DbContextOptionsBuilder<WorkspaceDbContext>().UseNpgsql(_connection).Options);

    private static PlatformSettingValue Value(string key, string scopeType, string scopeId, string json, Guid actor, DateTime at) => new()
    {
        SettingKey = key, ScopeType = scopeType, ScopeId = scopeId, ValueJson = json, Version = 1,
        CreatedAt = at, UpdatedAt = at, UpdatedBy = actor,
    };

    private static PlatformSettingChange Change(string key, string? before, string after, Guid actor, DateTime at) => new()
    {
        Id = Guid.NewGuid(), SettingKey = key, ScopeType = "platform", ScopeId = "", Action = "set",
        OldValueJson = before, NewValueJson = after, Version = 1, ChangedBy = actor, ChangedAt = at,
        ChangedByEmail = "ops@warptalk.vn",
    };

    private static string FindMigration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "workspace", "database", "migrations", Migration);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(Migration);
    }
}

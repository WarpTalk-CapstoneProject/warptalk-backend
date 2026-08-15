using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// Runs against real PostgreSQL, and does so on purpose.
///
/// Billing shipped an admin aggregation covered only by a mocked repository. The mock replaced
/// the exact LINQ that was broken, six tests passed throughout, and the endpoint returned 500 on
/// every call it ever served — a positional-record projection ordered by one of its own
/// properties, which EF cannot translate. `GetDirectoryAsync` is the same shape of query (filter,
/// sort, page, group), so it gets a real database from the start rather than after the outage.
///
/// It also exercises `EF.Functions.ILike`, which has no in-memory equivalent at all.
/// </summary>
public sealed class AdminUserDirectoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _activeId = Guid.NewGuid();
    private readonly Guid _lockedId = Guid.NewGuid();
    private readonly Guid _unverifiedId = Guid.NewGuid();
    private readonly Guid _deactivatedId = Guid.NewGuid();
    private readonly Guid _deletedId = Guid.NewGuid();
    private readonly Guid _adminRoleId = Guid.NewGuid();
    private readonly Guid _memberRoleId = Guid.NewGuid();

    private AuthDbContext _context = null!;
    private UserRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new AuthDbContext(
            new DbContextOptionsBuilder<AuthDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

        // The schema declares uuidv7() defaults; postgres:16 has no such builtin.
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
            DECLARE
                timestamp_ms bigint;
                timestamp_hex text;
                uuid_hex text;
            BEGIN
                timestamp_ms := (extract(epoch from clock_timestamp()) * 1000)::bigint;
                timestamp_hex := lpad(to_hex(timestamp_ms), 12, '0');
                uuid_hex := timestamp_hex || '7' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || '8' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || lpad(to_hex((random() * 281474976710655)::bigint), 12, '0');
                RETURN uuid_hex::uuid;
            END;
            $$ LANGUAGE plpgsql;
            """);
        await _context.Database.EnsureCreatedAsync();

        _repository = new UserRepository(_context);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        _context.Roles.AddRange(
            NewRole(_adminRoleId, "admin"),
            NewRole(_memberRoleId, "Member"));

        _context.Users.AddRange(
            NewUser(_activeId, "ada@warptalk.io.vn", "Ada Lovelace", createdAt: Anchor.AddDays(1),
                lastLoginAt: Anchor.AddDays(9)),
            // Locked by an unexpired lockout WINDOW rather than the flag — the case a filter that
            // only reads IsLocked misses entirely.
            NewUser(_lockedId, "grace@warptalk.io.vn", "Grace Hopper", createdAt: Anchor.AddDays(2),
                lockedUntil: DateTime.UtcNow.AddHours(1)),
            NewUser(_unverifiedId, "pending@acme.com", "Pending Person", createdAt: Anchor.AddDays(3),
                emailVerified: false),
            NewUser(_deactivatedId, "gone@acme.com", "Gone Away", createdAt: Anchor.AddDays(4),
                isActive: false),
            NewUser(_deletedId, "deleted@acme.com", "Deleted Person", createdAt: Anchor.AddDays(5),
                deletedAt: Anchor.AddDays(6)));

        _context.UserRoles.AddRange(
            NewUserRole(_activeId, _adminRoleId),
            NewUserRole(_activeId, _memberRoleId),
            // Revoked: a role somebody no longer holds must not appear, and must not match a filter.
            NewUserRole(_unverifiedId, _adminRoleId, revokedAt: Anchor.AddDays(7)));

        _context.RefreshTokens.AddRange(
            NewToken(_activeId, "chrome-mac"),
            NewToken(_activeId, "iphone"),
            NewToken(_activeId, "old-laptop", revokedAt: Anchor.AddDays(8)),
            NewToken(_activeId, "expired-tablet", expiresAt: Anchor.AddDays(2)));

        await _context.SaveChangesAsync();
    }

    private static Role NewRole(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        IsSystem = true,
        IsActive = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    private static User NewUser(
        Guid id,
        string email,
        string fullName,
        DateTime createdAt,
        bool isActive = true,
        bool emailVerified = true,
        DateTime? lockedUntil = null,
        DateTime? lastLoginAt = null,
        DateTime? deletedAt = null) => new()
    {
        Id = id,
        Email = email,
        FullName = fullName,
        PreferredLanguage = "en",
        Timezone = "UTC",
        IsActive = isActive,
        IsLocked = false,
        EmailVerified = emailVerified,
        LockedUntil = lockedUntil,
        LastLoginAt = lastLoginAt,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        DeletedAt = deletedAt,
    };

    private static UserRole NewUserRole(Guid userId, Guid roleId, DateTime? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        RoleId = roleId,
        AssignedAt = Anchor,
        RevokedAt = revokedAt,
    };

    private static RefreshToken NewToken(
        Guid userId,
        string device,
        DateTime? revokedAt = null,
        DateTime? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        FamilyId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        DeviceInfo = device,
        IpAddress = "127.0.0.1",
        CreatedAt = Anchor,
        ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(30),
        RevokedAt = revokedAt,
    };

    private static AdminUserDirectoryFilter Filter(
        string? search = null,
        string? status = null,
        string? role = null,
        string sort = "created_desc") => new(search, status, role, sort);

    [Fact]
    public async Task The_directory_query_translates_to_SQL_at_all()
    {
        // The regression guard. Everything below is unreachable if this throws.
        var exception = await Record.ExceptionAsync(
            () => _repository.GetDirectoryAsync(Filter(), 1, 20));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Deleted_accounts_are_excluded_unless_asked_for_by_name()
    {
        var (items, total) = await _repository.GetDirectoryAsync(Filter(), 1, 20);

        Assert.Equal(4, total);
        Assert.DoesNotContain(items, u => u.Id == _deletedId);

        var (deleted, deletedTotal) = await _repository.GetDirectoryAsync(Filter(status: "deleted"), 1, 20);
        Assert.Equal(1, deletedTotal);
        Assert.Equal(_deletedId, deleted[0].Id);
    }

    [Fact]
    public async Task Locked_matches_an_unexpired_lockout_window_not_only_the_flag()
    {
        var (items, _) = await _repository.GetDirectoryAsync(Filter(status: "locked"), 1, 20);

        Assert.Single(items);
        Assert.Equal(_lockedId, items[0].Id);
    }

    [Fact]
    public async Task Active_excludes_locked_unverified_and_deactivated()
    {
        var (items, _) = await _repository.GetDirectoryAsync(Filter(status: "active"), 1, 20);

        Assert.Single(items);
        Assert.Equal(_activeId, items[0].Id);
    }

    [Fact]
    public async Task Unverified_and_deactivated_are_separate_states()
    {
        var (unverified, _) = await _repository.GetDirectoryAsync(Filter(status: "unverified"), 1, 20);
        var (deactivated, _) = await _repository.GetDirectoryAsync(Filter(status: "deactivated"), 1, 20);

        Assert.Equal(_unverifiedId, Assert.Single(unverified).Id);
        Assert.Equal(_deactivatedId, Assert.Single(deactivated).Id);
    }

    [Fact]
    public async Task Search_matches_email_and_name_case_insensitively()
    {
        // ILike has no in-memory equivalent, so this assertion only means anything here.
        var (byEmail, _) = await _repository.GetDirectoryAsync(Filter(search: "ADA@warptalk"), 1, 20);
        var (byName, _) = await _repository.GetDirectoryAsync(Filter(search: "grace hopper"), 1, 20);

        Assert.Equal(_activeId, Assert.Single(byEmail).Id);
        Assert.Equal(_lockedId, Assert.Single(byName).Id);
    }

    [Fact]
    public async Task Roles_exclude_revoked_assignments()
    {
        var (items, _) = await _repository.GetDirectoryAsync(Filter(), 1, 20);

        var ada = items.Single(u => u.Id == _activeId);
        // Ordinal-ignore-case, so this order holds on any host. The default culture-aware sort
        // returns these two in a different order depending on the server's locale.
        Assert.Equal(new[] { "admin", "Member" }, ada.Roles);

        // The unverified account's admin assignment was revoked; it must not reappear as a role.
        var pending = items.Single(u => u.Id == _unverifiedId);
        Assert.Empty(pending.Roles);
    }

    [Fact]
    public async Task Filtering_by_role_ignores_revoked_assignments()
    {
        var (items, _) = await _repository.GetDirectoryAsync(Filter(role: "admin"), 1, 20);

        Assert.Equal(_activeId, Assert.Single(items).Id);
    }

    [Fact]
    public async Task Active_session_count_excludes_revoked_and_expired_tokens()
    {
        var (items, _) = await _repository.GetDirectoryAsync(Filter(), 1, 20);

        // Four tokens seeded: two live, one revoked, one expired.
        Assert.Equal(2, items.Single(u => u.Id == _activeId).ActiveSessionCount);
        Assert.Equal(0, items.Single(u => u.Id == _lockedId).ActiveSessionCount);
    }

    [Fact]
    public async Task Sorting_by_last_login_puts_accounts_that_never_signed_in_last()
    {
        var (items, _) = await _repository.GetDirectoryAsync(Filter(sort: "last_login_desc"), 1, 20);

        // Only Ada has ever signed in. PostgreSQL sorts NULL highest on DESC by default, which
        // would otherwise put three never-signed-in accounts above the one that just did.
        Assert.Equal(_activeId, items[0].Id);
        Assert.All(items.Skip(1), u => Assert.Null(u.LastLoginAt));
    }

    [Fact]
    public async Task Paging_reports_the_filtered_total_not_the_page_size()
    {
        var (items, total) = await _repository.GetDirectoryAsync(Filter(), 1, 2);

        Assert.Equal(2, items.Count);
        Assert.Equal(4, total);
    }

    [Fact]
    public async Task Detail_and_sessions_read_the_same_account()
    {
        var row = await _repository.GetDirectoryRowAsync(_activeId);
        var sessions = await _repository.GetActiveSessionsAsync(_activeId);

        Assert.NotNull(row);
        Assert.Equal("ada@warptalk.io.vn", row!.Email);
        Assert.Equal(2, row.ActiveSessionCount);
        Assert.Equal(2, sessions.Count);
        // Never the token or its hash — an administrator needs to know a session exists, not to
        // be able to use it.
        Assert.All(sessions, s => Assert.False(string.IsNullOrEmpty(s.DeviceInfo)));
    }

    [Fact]
    public async Task Detail_returns_null_for_an_unknown_account()
    {
        Assert.Null(await _repository.GetDirectoryRowAsync(Guid.NewGuid()));
    }
}

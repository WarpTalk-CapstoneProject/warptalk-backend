using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// The self-service session queries against real PostgreSQL: grouping by family and
/// ExecuteUpdate have no faithful mock, and a mocked repository is how an untranslatable query
/// once shipped behind green tests.
/// </summary>
public sealed class UserSessionRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _rotatedFamily = Guid.NewGuid();
    private readonly Guid _phoneFamily = Guid.NewGuid();
    private readonly Guid _deadFamily = Guid.NewGuid();
    private readonly Guid _otherFamily = Guid.NewGuid();

    private AuthDbContext _context = null!;
    private RefreshTokenRepository _repository = null!;

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

        _repository = new RefreshTokenRepository(_context);

        _context.Users.AddRange(NewUser(_me, "me@warptalk.io.vn"), NewUser(_other, "other@warptalk.io.vn"));
        _context.RefreshTokens.AddRange(
            // One laptop session that has rotated twice: signed in at +0, last active at +2h.
            NewToken(_me, _rotatedFamily, "laptop", Anchor, revokedAt: Anchor.AddHours(1)),
            NewToken(_me, _rotatedFamily, "laptop", Anchor.AddHours(1), revokedAt: Anchor.AddHours(2)),
            NewToken(_me, _rotatedFamily, "laptop", Anchor.AddHours(2)),
            NewToken(_me, _phoneFamily, "phone", Anchor.AddHours(3)),
            // Logged out: every row revoked. Not a session any more.
            NewToken(_me, _deadFamily, "old", Anchor, revokedAt: Anchor.AddHours(1)),
            NewToken(_other, _otherFamily, "someone else", Anchor.AddHours(4)));
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Lists_one_row_per_live_family_with_first_sign_in_and_last_activity()
    {
        var sessions = await _repository.GetActiveSessionsForUserAsync(_me);

        Assert.Equal(new[] { _phoneFamily, _rotatedFamily }, sessions.Select(s => s.FamilyId));
        var laptop = sessions.Single(s => s.FamilyId == _rotatedFamily);
        Assert.Equal(Anchor, laptop.SignedInAt);
        Assert.Equal(Anchor.AddHours(2), laptop.LastActiveAt);
    }

    [Fact]
    public async Task Revoke_others_leaves_the_kept_family_and_other_users_alone()
    {
        await _repository.RevokeAllForUserExceptFamilyAsync(_me, _rotatedFamily);
        _context.ChangeTracker.Clear();

        var mine = await _repository.GetActiveSessionsForUserAsync(_me);
        Assert.Equal(_rotatedFamily, Assert.Single(mine).FamilyId);
        Assert.Single(await _repository.GetActiveSessionsForUserAsync(_other));
    }

    private static User NewUser(Guid id, string email) => new()
    {
        Id = id,
        Email = email,
        FullName = email,
        PreferredLanguage = "en",
        Timezone = "UTC",
        IsActive = true,
        EmailVerified = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    private static RefreshToken NewToken(
        Guid userId, Guid familyId, string device, DateTime createdAt, DateTime? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        FamilyId = familyId,
        TokenHash = Guid.NewGuid().ToString("N"),
        DeviceInfo = device,
        IpAddress = "127.0.0.1",
        CreatedAt = createdAt,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        RevokedAt = revokedAt,
    };
}

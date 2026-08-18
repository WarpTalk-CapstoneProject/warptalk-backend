using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.AuthService.Tests.Infrastructure;

/// <summary>
/// The three privileged actions on a platform account, and the one guarantee that makes them
/// shippable: nothing happens unless it was recorded.
///
/// Against real PostgreSQL, and it has to be. The guarantee is implemented as an EF transaction
/// that is rolled back when the audit call fails, and a rollback is precisely what an in-memory
/// provider does not have — a mocked unit of work would report the change reverted whether or not
/// the real database ever reverted it. `RevokeAllForUserAsync` is also an `ExecuteUpdateAsync`,
/// which runs outside the change tracker and would be entirely invisible to a mock: this fixture
/// is the only thing that proves it enrols in the ambient transaction rather than committing on
/// its own.
/// </summary>
public sealed class AdminUserActionsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    private AuthDbContext _context = null!;
    private UnitOfWork _unitOfWork = null!;
    private IAdminAuditRecorder _audit = null!;
    private AdminUserService _service = null!;

    private AdminActorContext Actor => new(_actorId, "test-correlation");

    private static AdminUserActionRequest Request(string reason = "Reported compromised device") =>
        new(reason);

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

        var users = new UserRepository(_context);
        var tokens = new RefreshTokenRepository(_context);
        _unitOfWork = new UnitOfWork(
            _context,
            users,
            new RoleRepository(_context),
            new PermissionRepository(_context),
            new UserRoleRepository(_context),
            new UserSettingRepository(_context),
            tokens,
            new VoiceProfileRepository(_context),
            new VoiceSampleRepository(_context),
            new VoiceConsentRepository(_context));

        _audit = Substitute.For<IAdminAuditRecorder>();
        AuditSucceeds();

        _service = new AdminUserService(
            _unitOfWork,
            _audit,
            NullLogger<AdminUserService>.Instance,
            TimeProvider.System);

        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private void AuditSucceeds() =>
        _audit
            .RecordAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>>(),
                Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

    /// <summary>The workspace service refusing, or being unreachable. Same answer either way.</summary>
    private void AuditFails() =>
        _audit
            .RecordAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>>(),
                Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                Result.Failure("audit log unreachable", ErrorCodes.InternalServerError)));

    private async Task SeedAsync()
    {
        // The actor is a real account, and has to be: `users.updated_by` is a foreign key back
        // into this table, so an administrator who is not a row here cannot be recorded as having
        // touched anybody. A fixture that skipped this would fail on the constraint and read as a
        // bug in the action rather than as a missing row in the test.
        _context.Users.Add(NewUser(_actorId, "admin@warptalk.io.vn", "Admin Master"));
        _context.Users.Add(NewUser(_userId, "ada@warptalk.io.vn", "Ada Lovelace"));

        _context.RefreshTokens.AddRange(
            NewToken("chrome-mac"),
            NewToken("iphone"));

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static User NewUser(Guid id, string email, string fullName) => new()
    {
        Id = id,
        Email = email,
        FullName = fullName,
        PasswordHash = "hash",
        PreferredLanguage = "en",
        Timezone = "UTC",
        IsActive = true,
        IsLocked = false,
        FailedLoginAttempts = 0,
        EmailVerified = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
    };

    private RefreshToken NewToken(string device) => new()
    {
        Id = Guid.NewGuid(),
        UserId = _userId,
        FamilyId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        DeviceInfo = device,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<int> LiveSessionCountAsync()
    {
        _context.ChangeTracker.Clear();
        return await _context.RefreshTokens.CountAsync(
            t => t.UserId == _userId && t.RevokedAt == null && t.ExpiresAt > DateTime.UtcNow);
    }

    private async Task<User> ReadUserAsync()
    {
        _context.ChangeTracker.Clear();
        return await _context.Users.AsNoTracking().SingleAsync(u => u.Id == _userId);
    }

    // ── the guarantee ───────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeSessions_leaves_the_sessions_running_when_the_audit_fails()
    {
        AuditFails();

        var result = await _service.RevokeSessionsAsync(_userId, Actor, Request());

        Assert.False(result.IsSuccess);
        // The whole point. An ExecuteUpdateAsync had already run against the connection; the
        // rollback is what puts it back, and only a real database can say whether it did.
        Assert.Equal(2, await LiveSessionCountAsync());
    }

    [Fact]
    public async Task Deactivate_leaves_the_account_active_when_the_audit_fails()
    {
        AuditFails();

        var result = await _service.SetAccountActiveAsync(_userId, isActive: false, Actor, Request());

        Assert.False(result.IsSuccess);
        var user = await ReadUserAsync();
        Assert.True(user.IsActive);
        // Deactivating also ends sessions, so both halves of the change must come back.
        Assert.Equal(2, await LiveSessionCountAsync());
    }

    [Fact]
    public async Task A_failed_audit_reports_why_rather_than_a_bare_error()
    {
        AuditFails();

        var result = await _service.UnlockAsync(_userId, Actor, Request());

        Assert.False(result.IsSuccess);
        Assert.Contains("audit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── the actions themselves ──────────────────────────────────────────────

    [Fact]
    public async Task RevokeSessions_ends_every_live_session()
    {
        var result = await _service.RevokeSessionsAsync(_userId, Actor, Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(0, await LiveSessionCountAsync());
        // The rows survive their revocation: the account's history still shows it was signed in.
        Assert.Equal(2, await _context.RefreshTokens.CountAsync(t => t.UserId == _userId));
        Assert.Empty(result.Value!.ActiveSessions);
    }

    [Fact]
    public async Task RevokeSessions_does_not_lock_or_deactivate_the_account()
    {
        // The response to "signed in somewhere they should not be", not a punishment. Conflating
        // the two would make it unusable for the case it exists for.
        await _service.RevokeSessionsAsync(_userId, Actor, Request());

        var user = await ReadUserAsync();
        Assert.True(user.IsActive);
        Assert.False(user.IsLocked);
    }

    [Fact]
    public async Task Deactivate_turns_the_account_off_and_ends_its_sessions()
    {
        var result = await _service.SetAccountActiveAsync(_userId, isActive: false, Actor, Request());

        Assert.True(result.IsSuccess);
        Assert.False((await ReadUserAsync()).IsActive);
        // Otherwise the account stays usable until each token happens to expire.
        Assert.Equal(0, await LiveSessionCountAsync());
    }

    [Fact]
    public async Task Reactivate_turns_it_back_on_without_restoring_the_sessions()
    {
        await _service.SetAccountActiveAsync(_userId, isActive: false, Actor, Request());

        var result = await _service.SetAccountActiveAsync(_userId, isActive: true, Actor, Request());

        Assert.True(result.IsSuccess);
        Assert.True((await ReadUserAsync()).IsActive);
        // A revoked token stays revoked. The person signs in again; nothing resurrects.
        Assert.Equal(0, await LiveSessionCountAsync());
    }

    [Fact]
    public async Task Unlock_clears_the_window_and_the_counter()
    {
        var locked = await _context.Users.SingleAsync(u => u.Id == _userId);
        locked.IsLocked = true;
        locked.LockedUntil = DateTime.UtcNow.AddHours(1);
        locked.FailedLoginAttempts = 5;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _service.UnlockAsync(_userId, Actor, Request());

        Assert.True(result.IsSuccess);
        var user = await ReadUserAsync();
        Assert.False(user.IsLocked);
        Assert.Null(user.LockedUntil);
        // Leaving the counter at its limit would re-lock on the next single mistyped password.
        Assert.Equal(0, user.FailedLoginAttempts);
    }

    // ── what the audit entry says ───────────────────────────────────────────

    [Fact]
    public async Task The_entry_names_the_actor_from_the_token_and_the_reason_given()
    {
        await _service.RevokeSessionsAsync(_userId, Actor, Request("Laptop stolen at the airport"));

        await _audit.Received(1).RecordAsync(
            AdminAuditUserActions.SessionsRevoked,
            _userId,
            _actorId,
            "Laptop stolen at the airport",
            "test-correlation",
            Arg.Any<IReadOnlyDictionary<string, string?>>(),
            Arg.Any<IReadOnlyDictionary<string, string?>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_entry_records_how_many_sessions_were_actually_ended()
    {
        await _service.RevokeSessionsAsync(_userId, Actor, Request());

        await _audit.Received(1).RecordAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            // Read BEFORE the revoke. Reading it after would record zero ended on every entry.
            Arg.Is<IReadOnlyDictionary<string, string?>>(before => before["active_sessions"] == "2"),
            Arg.Is<IReadOnlyDictionary<string, string?>>(after => after["active_sessions"] == "0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deactivate_and_reactivate_are_recorded_as_different_actions()
    {
        await _service.SetAccountActiveAsync(_userId, isActive: false, Actor, Request());
        await _service.SetAccountActiveAsync(_userId, isActive: true, Actor, Request());

        await _audit.Received(1).RecordAsync(
            AdminAuditUserActions.Deactivated, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>>(),
            Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            AdminAuditUserActions.Reactivated, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string?>>(),
            Arg.Any<IReadOnlyDictionary<string, string?>>(), Arg.Any<CancellationToken>());
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_blank_reason_is_refused_before_anything_is_touched()
    {
        var result = await _service.RevokeSessionsAsync(_userId, Actor, Request("   "));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        Assert.Equal(2, await LiveSessionCountAsync());
        // Nothing was attempted, so nothing was recorded either.
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default, default!, default!, default, default, default);
    }

    [Fact]
    public async Task An_unknown_user_is_a_not_found_rather_than_a_silent_success()
    {
        var result = await _service.RevokeSessionsAsync(Guid.NewGuid(), Actor, Request());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }
}

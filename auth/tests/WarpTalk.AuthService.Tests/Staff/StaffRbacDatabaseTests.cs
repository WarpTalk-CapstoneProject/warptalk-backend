using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Helpers;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Application.Services;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Infrastructure.Persistence;
using WarpTalk.AuthService.Infrastructure.Repositories;
using WarpTalk.AuthService.Infrastructure.Security;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.AuthService.Tests.Staff;

/// <summary>
/// G10 against real PostgreSQL and the REAL migration file: the migration of existing system
/// admins, what each role resolves to, the guard rails through the service, the audit-or-rollback
/// guarantee, and what the access token carries.
///
/// Uses Testcontainers like the other database tests here. Set WARPTALK_TEST_POSTGRES to a
/// connection string for a server you already run (the test creates and drops its own database)
/// to run it without Docker.
/// </summary>
public sealed class StaffRbacDatabaseTests : IAsyncLifetime
{
    /// <summary>
    /// The RBAC migration and every permission migration after it, applied in order — what
    /// production runs. The seeded catalog is held to AdminPermissions over all of them.
    /// </summary>
    private static readonly string[] MigrationFiles =
    [
        "20260925090000_add_platform_staff_rbac.sql",
        "20260925170000_add_billing_packages_manage_permission.sql",
    ];

    private PostgreSqlContainer? _container;
    private string _adminConnection = null!;
    private string _databaseName = null!;
    private AuthDbContext _context = null!;
    private UnitOfWork _unitOfWork = null!;
    private IAdminAuditRecorder _audit = null!;
    private IAuthEmailSender _email = null!;
    private StaffAccessService _access = null!;
    private StaffAdminService _staff = null!;

    private Guid _legacyAdminRoleId;
    private Guid _userRoleId;

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────

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

        _databaseName = "g10_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName }.ConnectionString;
        _context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
        // The schema declares uuidv7() defaults; postgres 16 has no such builtin.
        await _context.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$ SELECT gen_random_uuid() $$ LANGUAGE sql;");
        await _context.Database.EnsureCreatedAsync();

        // The legacy roles init-db.sql seeds in every environment.
        _legacyAdminRoleId = await InsertLegacyRoleAsync("admin");
        _userRoleId = await InsertLegacyRoleAsync("user");
        await InsertLegacyRoleAsync("Admin");

        _unitOfWork = new UnitOfWork(
            _context,
            new UserRepository(_context),
            new RoleRepository(_context),
            new PermissionRepository(_context),
            new UserRoleRepository(_context),
            new UserSettingRepository(_context),
            new RefreshTokenRepository(_context),
            new VoiceProfileRepository(_context),
            new VoiceSampleRepository(_context),
            new VoiceConsentRepository(_context),
            new StaffMemberRepository(_context),
            new StaffInvitationRepository(_context));

        _audit = Substitute.For<IAdminAuditRecorder>();
        _audit.RecordSubjectAsync(Arg.Any<AdminAuditSubjectRecord>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _audit.RecordAsync(default!, default, default, default!, default!, default)
            .ReturnsForAnyArgs(Result.Success());
        _email = Substitute.For<IAuthEmailSender>();

        _access = new StaffAccessService(_unitOfWork, NullLogger<StaffAccessService>.Instance, _audit);
        // No cache: each check reads what the previous step wrote.
        var resolver = new CachedStaffAccessResolver(
            new DelegateStaffAccessSource(id => _access.GetAccessAsync(id).GetAwaiter().GetResult()),
            new MemoryCache(new MemoryCacheOptions()),
            new Fixed(new StaffAuthorizationOptions { CacheSeconds = 0 }),
            NullLogger<CachedStaffAccessResolver>.Instance);
        _staff = new StaffAdminService(_unitOfWork, _access, resolver, _audit, _email, NullLogger<StaffAdminService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        if (_container is not null) await _container.DisposeAsync();
    }

    private async Task<Guid> InsertLegacyRoleAsync(string name)
    {
        var id = Guid.NewGuid();
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO auth.roles (id, name, is_system, is_active, created_at, updated_at) VALUES ({id}, {name}, true, true, now(), now())");
        return id;
    }

    private async Task RunMigrationAsync()
    {
        foreach (var file in MigrationFiles)
        {
            var sql = await File.ReadAllTextAsync(FindMigration(file));
            await _context.Database.ExecuteSqlRawAsync(sql);
        }

        _context.ChangeTracker.Clear();
    }

    private static string FindMigration(string migrationFile)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "auth", "database", "migrations", migrationFile);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(migrationFile);
    }

    private async Task<User> AddUserAsync(string email, bool verified = true, bool legacyAdmin = false, bool legacyRevoked = false)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = email.Split('@')[0],
            PasswordHash = "x",
            PreferredLanguage = "en",
            Timezone = "UTC",
            IsActive = true,
            EmailVerified = verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.Users.Add(user);
        _context.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = _userRoleId, AssignedAt = DateTime.UtcNow });
        if (legacyAdmin)
        {
            _context.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleId = _legacyAdminRoleId,
                AssignedAt = DateTime.UtcNow,
                RevokedAt = legacyRevoked ? DateTime.UtcNow : null,
            });
        }

        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> RoleIdAsync(string slug) =>
        (await _context.Roles.AsNoTracking().SingleAsync(r => r.Slug == slug)).Id;

    private async Task<User> StaffAsync(string email, string roleSlug)
    {
        var user = await AddUserAsync(email);
        _context.StaffMembers.Add(new StaffMember
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = await RoleIdAsync(roleSlug),
            Status = StaffConstants.Statuses.Active,
            Source = StaffConstants.Sources.Invited,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return user;
    }

    /// <summary>A custom role that can manage staff but holds only a little else — the escalation suspect.</summary>
    private async Task<User> StaffLeadAsync()
    {
        var superAdmin = await StaffAsync("root-for-lead@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var role = await _staff.CreateRoleAsync(Actor(superAdmin), new SaveStaffRoleRequest(
            "Staff lead", null, [AdminPermissions.StaffRead, AdminPermissions.StaffManage, AdminPermissions.WorkspacesRead], null));
        Assert.True(role.IsSuccess, role.Error);
        return await StaffAsync("lead@warptalk.test", role.Value!.Slug);
    }

    private static AdminActorContext Actor(User user) => new(user.Id, "corr-" + Guid.NewGuid().ToString("N"));

    private static StaffActionRequest Because(string reason = "Left the company") => new(reason);

    // ── Migration ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_MakesEveryLiveLegacyAdminASuperAdmin_AndNobodyElse_Idempotently()
    {
        var live = await AddUserAsync("live-admin@warptalk.test", legacyAdmin: true);
        var revoked = await AddUserAsync("revoked-admin@warptalk.test", legacyAdmin: true, legacyRevoked: true);
        var deleted = await AddUserAsync("deleted-admin@warptalk.test", legacyAdmin: true);
        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE auth.users SET deleted_at = now() WHERE id = {deleted.Id}");
        var plain = await AddUserAsync("plain@warptalk.test");

        await RunMigrationAsync();
        await RunMigrationAsync();

        var staff = await _context.StaffMembers.AsNoTracking().Include(m => m.Role).ToListAsync();
        var member = Assert.Single(staff);
        Assert.Equal(live.Id, member.UserId);
        Assert.Equal(BuiltInStaffRoles.SuperAdmin, member.Role.Slug);
        Assert.Equal(StaffConstants.Sources.Migrated, member.Source);
        Assert.DoesNotContain(staff, m => m.UserId == revoked.Id || m.UserId == deleted.Id || m.UserId == plain.Id);

        // The legacy rows stay: rolling back to pre-G10 code keeps every admin working.
        Assert.Equal(3, await _context.UserRoles.CountAsync(ur => ur.RoleId == _legacyAdminRoleId));
    }

    [Fact]
    public async Task Migration_SeedsExactlyTheCatalogAndTheBuiltInRoles()
    {
        await RunMigrationAsync();

        var codes = await _context.Permissions.AsNoTracking().Select(p => p.Code).ToListAsync();
        Assert.Equal(AdminPermissions.All.OrderBy(c => c), codes.OrderBy(c => c));

        foreach (var definition in BuiltInStaffRoles.Definitions)
        {
            var role = await _context.Roles.AsNoTracking()
                .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                .SingleAsync(r => r.Slug == definition.Slug);
            Assert.True(role.IsSystem);
            Assert.Equal(StaffConstants.RoleScopes.PlatformStaff, role.Scope);
            Assert.Equal(definition.Name, role.Name);

            var stored = role.RolePermissions.Select(rp => rp.Permission.Code).OrderBy(c => c).ToList();
            // Super Admin is every permission by rule; no rows.
            var expected = definition.Slug == BuiltInStaffRoles.SuperAdmin ? [] : definition.Permissions.OrderBy(c => c).ToList();
            Assert.Equal(expected, stored);
        }
    }

    // ── Policy resolution ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachRole_ResolvesToItsPermissions_AndSuperAdminToAll()
    {
        await RunMigrationAsync();
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var auditor = await StaffAsync("auditor@warptalk.test", BuiltInStaffRoles.ReadOnlyAuditor);

        var supportAccess = await _access.GetAccessAsync(support.Id);
        Assert.True(supportAccess.Has(AdminPermissions.WorkspacesWrite));
        Assert.False(supportAccess.Has(AdminPermissions.BillingAdjustCredit));
        Assert.False(supportAccess.Has(AdminPermissions.StaffManage));

        var rootAccess = await _access.GetAccessAsync(root.Id);
        Assert.True(rootAccess.IsSuperAdmin);
        Assert.All(AdminPermissions.All, code => Assert.True(rootAccess.Has(code)));

        var auditorAccess = await _access.GetAccessAsync(auditor.Id);
        Assert.True(auditorAccess.Has(AdminPermissions.AuditExport));
        Assert.False(auditorAccess.Has(AdminPermissions.WorkspacesLifecycle));
    }

    [Fact]
    public async Task SuspendedStaff_AndStaffWithADeactivatedAccount_HaveNoAccess()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);
        var ops = await StaffAsync("ops@warptalk.test", BuiltInStaffRoles.OperationsSre);

        var suspended = await _staff.SuspendAsync(Actor(root), support.Id, Because("Investigating a complaint"));
        Assert.True(suspended.IsSuccess, suspended.Error);
        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE auth.users SET is_active = false WHERE id = {ops.Id}");
        _context.ChangeTracker.Clear();

        Assert.False((await _access.GetAccessAsync(support.Id)).IsStaff);
        Assert.False((await _access.GetAccessAsync(ops.Id)).IsStaff);
    }

    [Fact]
    public async Task ALegacyAdminWithNoStaffRow_IsEnrolledAsSuperAdmin_SoASeedRunLateLocksNobodyOut()
    {
        await RunMigrationAsync();
        // Granted the legacy role AFTER the migration ran — seed-demo.sql on a fresh stack.
        var late = await AddUserAsync("late-seed@warptalk.test", legacyAdmin: true);

        var access = await _access.GetAccessAsync(late.Id);

        Assert.True(access.IsSuperAdmin);
        var member = await _context.StaffMembers.AsNoTracking().SingleAsync(m => m.UserId == late.Id);
        Assert.Equal(StaffConstants.Sources.LegacyBridge, member.Source);
    }

    [Fact]
    public async Task RemovingASuperAdmin_DeletesTheirLegacyRole_SoARollbackCannotRestoreIt()
    {
        var removed = await AddUserAsync("migrated-b@warptalk.test", legacyAdmin: true);
        var remover = await AddUserAsync("migrated-a@warptalk.test", legacyAdmin: true);
        await RunMigrationAsync();

        var result = await _staff.RemoveAsync(Actor(remover), removed.Id, Because());

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(await _context.UserRoles.AnyAsync(ur => ur.UserId == removed.Id && ur.RoleId == _legacyAdminRoleId));
        Assert.False((await _access.GetAccessAsync(removed.Id)).IsStaff);
    }

    // ── Guard rails through the service ──────────────────────────────────────────────────────

    [Fact]
    public async Task AStaffManager_CannotEscalate_ByAnyRoute()
    {
        await RunMigrationAsync();
        var lead = await StaffLeadAsync();
        var leadRoleId = (await _context.StaffMembers.AsNoTracking().SingleAsync(m => m.UserId == lead.Id)).RoleId;
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);

        // Their own role, to a bigger one.
        var ownRole = await _staff.ChangeRoleAsync(Actor(lead), lead.Id,
            new ChangeStaffRoleRequest(await RoleIdAsync(BuiltInStaffRoles.SuperAdmin), "promote myself"));
        // The role they hold, widened.
        var widen = await _staff.UpdateRoleAsync(Actor(lead), leadRoleId, new SaveStaffRoleRequest(
            "Staff lead", null, [AdminPermissions.StaffRead, AdminPermissions.StaffManage, AdminPermissions.BillingAdjustCredit], "widen"));
        // A new role with more than they have — to hand to an accomplice.
        var bigger = await _staff.CreateRoleAsync(Actor(lead), new SaveStaffRoleRequest(
            "Accomplice", null, [AdminPermissions.BillingAdjustCredit], null));
        // Super Admin, or a role holding more than they do, for someone else.
        var grantSuper = await _staff.ChangeRoleAsync(Actor(lead), support.Id,
            new ChangeStaffRoleRequest(await RoleIdAsync(BuiltInStaffRoles.SuperAdmin), "promote"));
        var inviteFinance = await _staff.InviteAsync(Actor(lead),
            new InviteStaffRequest("new@warptalk.test", await RoleIdAsync(BuiltInStaffRoles.BillingFinance), null));
        // Acting on someone who holds more than they do.
        var suspendSupport = await _staff.SuspendAsync(Actor(lead), support.Id, Because());

        Assert.All(
            new Result[] { ownRole, widen, bigger, grantSuper, inviteFinance, suspendSupport },
            result => Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode));
        Assert.Equal(StaffGuards.SelfMessage, ownRole.Error);
        Assert.Equal(StaffGuards.OwnRoleMessage, widen.Error);
        Assert.False(await _context.StaffInvitations.AnyAsync());
    }

    [Fact]
    public async Task TheLastActiveSuperAdmin_CannotSwitchOffTheirOwnAccount()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var accounts = new AdminUserService(_unitOfWork, _audit, NullLogger<AdminUserService>.Instance, staffAccess: _access);

        var result = await accounts.SetAccountActiveAsync(root.Id, false, Actor(root), new AdminUserActionRequest("closing my account"));

        Assert.Equal(ErrorCodes.Conflict, result.ErrorCode);
        Assert.Equal(StaffGuards.LastSuperAdminMessage, result.Error);
        Assert.True((await _context.Users.AsNoTracking().SingleAsync(u => u.Id == root.Id)).IsActive);
    }

    [Fact]
    public async Task AccountsManage_IsNotABackDoor_ToSwitchingOffASuperAdmin()
    {
        await RunMigrationAsync();
        await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var root2 = await StaffAsync("root2@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);
        var accounts = new AdminUserService(_unitOfWork, _audit, NullLogger<AdminUserService>.Instance, staffAccess: _access);

        var result = await accounts.SetAccountActiveAsync(root2.Id, false, Actor(support), new AdminUserActionRequest("nope"));

        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
    }

    [Fact]
    public async Task ARoleChangeTheAuditStoreRefuses_IsRolledBack()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);
        _audit.RecordSubjectAsync(Arg.Any<AdminAuditSubjectRecord>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("audit store down", ErrorCodes.InternalServerError));

        var result = await _staff.ChangeRoleAsync(Actor(root), support.Id,
            new ChangeStaffRoleRequest(await RoleIdAsync(BuiltInStaffRoles.ReadOnlyAuditor), "reshuffle"));

        Assert.False(result.IsSuccess);
        _context.ChangeTracker.Clear();
        var member = await _context.StaffMembers.AsNoTracking().Include(m => m.Role).SingleAsync(m => m.UserId == support.Id);
        Assert.Equal(BuiltInStaffRoles.Support, member.Role.Slug);
    }

    [Fact]
    public async Task EveryStaffWrite_IsAuditedWithTheActorAndTheReason()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);

        await _staff.SuspendAsync(Actor(root), support.Id, Because("Holiday"));

        await _audit.Received(1).RecordSubjectAsync(
            Arg.Is<AdminAuditSubjectRecord>(r =>
                r.Action == AdminAuditStaffActions.Suspended
                && r.EntityType == AdminAuditEntityTypes.StaffMember
                && r.EntityId == support.Id
                && r.ActorId == root.Id
                && r.Reason == "Holiday"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DestructiveActions_NeedAReason()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var support = await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);

        Assert.Equal(ErrorCodes.ValidationError, (await _staff.RemoveAsync(Actor(root), support.Id, Because("  "))).ErrorCode);
        Assert.Equal(ErrorCodes.ValidationError, (await _staff.SuspendAsync(Actor(root), support.Id, new StaffActionRequest(null))).ErrorCode);
    }

    [Fact]
    public async Task Roles_BuiltInAreReadOnly_InUseCannotBeDeleted_UnusedCan()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var custom = await _staff.DuplicateRoleAsync(Actor(root), await RoleIdAsync(BuiltInStaffRoles.Support), new DuplicateStaffRoleRequest(null));
        Assert.True(custom.IsSuccess, custom.Error);
        Assert.Equal("Support (copy)", custom.Value!.Name);
        Assert.False(custom.Value.IsBuiltIn);

        var builtIn = await _staff.DeleteRoleAsync(Actor(root), await RoleIdAsync(BuiltInStaffRoles.Support), Because("tidy"));
        Assert.Equal(ErrorCodes.Forbidden, builtIn.ErrorCode);

        var holder = await StaffAsync("holder@warptalk.test", custom.Value.Slug);
        var inUse = await _staff.DeleteRoleAsync(Actor(root), custom.Value.Id, Because("tidy"));
        Assert.Equal(ErrorCodes.Conflict, inUse.ErrorCode);

        await _staff.RemoveAsync(Actor(root), holder.Id, Because());
        var deleted = await _staff.DeleteRoleAsync(Actor(root), custom.Value.Id, Because("tidy"));
        Assert.True(deleted.IsSuccess, deleted.Error);
        Assert.DoesNotContain((await _staff.ListRolesAsync()).Value!, r => r.Id == custom.Value.Id);
    }

    [Fact]
    public async Task WhoHasThisPermission_NamesTheRolesAndThePeople()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var finance = await StaffAsync("finance@warptalk.test", BuiltInStaffRoles.BillingFinance);
        await StaffAsync("support@warptalk.test", BuiltInStaffRoles.Support);

        var holders = await _staff.GetPermissionHoldersAsync(AdminPermissions.BillingAdjustCredit);

        Assert.True(holders.IsSuccess);
        Assert.Equal(
            [BuiltInStaffRoles.BillingFinance, BuiltInStaffRoles.SuperAdmin],
            holders.Value!.Roles.Select(r => r.Slug).OrderBy(s => s));
        Assert.Equal(new[] { finance.Id, root.Id }.OrderBy(id => id), holders.Value.Members.Select(m => m.UserId).OrderBy(id => id));
    }

    // ── Invitations and the token ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InvitingAnExistingAccount_GrantsAtOnce_AndTheNextTokenSaysAdmin()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var person = await AddUserAsync("person@warptalk.test");

        var invited = await _staff.InviteAsync(Actor(root),
            new InviteStaffRequest("Person@WarpTalk.test ", await RoleIdAsync(BuiltInStaffRoles.Support), "new hire"));

        Assert.True(invited.IsSuccess, invited.Error);
        Assert.Equal("granted", invited.Value!.Outcome);
        var token = await TokenForAsync(person.Id);
        Assert.Contains(token.Claims, c => c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role" && c.Value == "admin");
        Assert.Contains(token.Claims, c => c.Type == StaffClaims.StaffRole && c.Value == BuiltInStaffRoles.Support);
    }

    [Fact]
    public async Task AnInvitationToANewAddress_ActivatesOnlyOnAVerifiedSignIn()
    {
        await RunMigrationAsync();
        var root = await StaffAsync("root@warptalk.test", BuiltInStaffRoles.SuperAdmin);
        var invited = await _staff.InviteAsync(Actor(root),
            new InviteStaffRequest("newcomer@warptalk.test", await RoleIdAsync(BuiltInStaffRoles.ContentMarketing), null));
        Assert.Equal("invited", invited.Value!.Outcome);
        await _email.Received(1).SendStaffInvitationEmailAsync("newcomer@warptalk.test", Arg.Any<string>(), "Content / Marketing", Arg.Any<CancellationToken>());

        var newcomer = await AddUserAsync("newcomer@warptalk.test", verified: false);
        var unverified = await TokenForAsync(newcomer.Id);
        Assert.DoesNotContain(unverified.Claims, c => c.Type == StaffClaims.StaffRole);

        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE auth.users SET email_verified = true WHERE id = {newcomer.Id}");
        _context.ChangeTracker.Clear();
        var verified = await TokenForAsync(newcomer.Id);
        Assert.Contains(verified.Claims, c => c.Type == StaffClaims.StaffRole && c.Value == BuiltInStaffRoles.ContentMarketing);
        Assert.Equal("accepted", (await _staff.ListInvitationsAsync()).Value!.Single().Status);
    }

    [Fact]
    public async Task ASuspendedStaffMembersNextToken_DoesNotSayAdmin_EvenWithALegacyRow()
    {
        var legacy = await AddUserAsync("legacy@warptalk.test", legacyAdmin: true);
        var root = await AddUserAsync("root@warptalk.test", legacyAdmin: true);
        await RunMigrationAsync();

        await _staff.SuspendAsync(Actor(root), legacy.Id, Because());
        var token = await TokenForAsync(legacy.Id);

        Assert.DoesNotContain(token.Claims, c => c.Value == "admin");
    }

    private async Task<JwtSecurityToken> TokenForAsync(Guid userId)
    {
        _context.ChangeTracker.Clear();
        var user = await _unitOfWork.UserRepository.GetByIdWithRolesAsync(userId);
        var generator = new JwtTokenGenerator(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "g10-test-secret-that-is-long-enough-for-hmac-sha256!!",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
        }).Build());
        var response = await AuthResponseHelper.CreateAuthResponseAsync(
            user!, null, null, generator, _unitOfWork.RefreshTokenRepository, _unitOfWork, "user", CancellationToken.None,
            staffAccess: _access);
        return new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
    }

    private sealed class Fixed(StaffAuthorizationOptions value) : IOptionsMonitor<StaffAuthorizationOptions>
    {
        public StaffAuthorizationOptions CurrentValue => value;
        public StaffAuthorizationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StaffAuthorizationOptions, string?> listener) => null;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>What the token carries about a staff member: a hint for the UI, never the authority.</summary>
public sealed record StaffTokenGrant(string RoleSlug);

/// <summary>
/// The auth service's own answer to "who is staff, with what" (G10). Served to every other service
/// through UserService.GetStaffAccess and to this one through <see cref="DatabaseStaffAccessSource"/>.
/// </summary>
public interface IStaffAccessService
{
    /// <summary>Live access from the database. Touches last_active_at (at most every 5 minutes).</summary>
    Task<StaffAccess> GetAccessAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Called while issuing a token (login, Google, refresh, verification). Accepts a pending
    /// invitation for the account's VERIFIED address, enrols a legacy 'admin' holder, and returns
    /// the grant the token should carry — null when the person is not active staff.
    /// </summary>
    Task<StaffTokenGrant?> ResolveForTokenAsync(User user, CancellationToken ct = default);

    /// <summary>Read-only: the active staff role slug, or null. For responses that echo the token's roles.</summary>
    Task<string?> ActiveRoleSlugAsync(Guid userId, CancellationToken ct = default);
}

public sealed class StaffAccessService : IStaffAccessService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAdminAuditRecorder? _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<StaffAccessService> _logger;

    public StaffAccessService(
        IUnitOfWork unitOfWork,
        ILogger<StaffAccessService> logger,
        IAdminAuditRecorder? audit = null,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    public async Task<StaffAccess> GetAccessAsync(Guid userId, CancellationToken ct = default)
    {
        var member = await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(userId, ct)
            ?? await TryEnrolLegacyAdminAsync(userId, ct);
        if (member is null || member.Status != StaffConstants.Statuses.Active) return StaffAccess.None;

        var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user is null || user.DeletedAt is not null || !user.IsActive) return StaffAccess.None;

        await TouchLastActiveAsync(member, ct);
        return ToAccess(member.Role);
    }

    public async Task<StaffTokenGrant?> ResolveForTokenAsync(User user, CancellationToken ct = default)
    {
        if (user.DeletedAt is not null || !user.IsActive) return null;

        var member = await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(user.Id, ct)
            ?? await TryEnrolLegacyAdminAsync(user.Id, ct)
            ?? await TryAcceptInvitationAsync(user, ct);

        return member is { Status: StaffConstants.Statuses.Active, Role.Slug: { } slug }
            ? new StaffTokenGrant(slug)
            : null;
    }

    public async Task<string?> ActiveRoleSlugAsync(Guid userId, CancellationToken ct = default)
    {
        var member = await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(userId, ct);
        return member is { Status: StaffConstants.Statuses.Active } ? member.Role?.Slug : null;
    }

    /// <summary>The access a role confers. Super Admin is every permission by rule, not by rows.</summary>
    public static StaffAccess ToAccess(Role role)
    {
        var isSuper = string.Equals(role.Slug, BuiltInStaffRoles.SuperAdmin, StringComparison.Ordinal);
        var codes = isSuper
            ? new HashSet<string>(StringComparer.Ordinal)
            : RoleCodes(role).ToHashSet(StringComparer.Ordinal);
        return new StaffAccess(true, role.Slug, role.Name, isSuper, codes);
    }

    /// <summary>The permission codes stored for a role (active, and known to the catalog).</summary>
    public static IReadOnlyList<string> RoleCodes(Role role) =>
        role.RolePermissions
            .Where(rp => rp.Permission is { IsActive: true, DeletedAt: null })
            .Select(rp => rp.Permission.Code)
            .Where(AdminPermissions.IsKnown)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The effective codes a role grants: every catalog code for Super Admin.</summary>
    public static IReadOnlyList<string> EffectiveCodes(Role role) => ToAccess(role).EffectivePermissions();

    /// <summary>
    /// LOCK-OUT GUARD. A legacy 'admin' holder with no staff row becomes a Super Admin.
    ///
    /// The migration enrols every such person once, but anything that grants the legacy role
    /// afterwards — seed-demo.sql run after migrations on a fresh stack, a restore, a rollback and
    /// roll-forward — would otherwise leave a platform administrator who can no longer reach the
    /// portal. A removed staff member is not re-enrolled: removal deletes their legacy row too.
    /// </summary>
    private async Task<StaffMember?> TryEnrolLegacyAdminAsync(Guid userId, CancellationToken ct)
    {
        if (!await _unitOfWork.StaffMemberRepository.HoldsLegacyAdminRoleAsync(userId, ct)) return null;

        var superAdmin = await _unitOfWork.RoleRepository.GetStaffRoleBySlugAsync(BuiltInStaffRoles.SuperAdmin, ct);
        if (superAdmin is null)
        {
            _logger.LogError(
                "User {UserId} holds the legacy 'admin' role but no Super Admin staff role exists; has the G10 migration run?",
                userId);
            return null;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var member = new StaffMember
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = superAdmin.Id,
            Role = superAdmin,
            Status = StaffConstants.Statuses.Active,
            Source = StaffConstants.Sources.LegacyBridge,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await _unitOfWork.StaffMemberRepository.AddAsync(member, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Enrolled legacy platform admin {UserId} as Super Admin (no staff row existed).", userId);
            return member;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely a concurrent request enrolled them first (unique user_id). Re-read.
            _logger.LogInformation(ex, "Legacy admin enrolment for {UserId} raced; re-reading.", userId);
            _unitOfWork.StaffMemberRepository.Remove(member);
            return await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(userId, ct);
        }
    }

    private async Task<StaffMember?> TryAcceptInvitationAsync(User user, CancellationToken ct)
    {
        // The whole security of an address-bound invitation is that the address was proven.
        if (!user.EmailVerified) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        var email = user.Email.Trim().ToLowerInvariant();
        var invitation = await _unitOfWork.StaffInvitationRepository.GetPendingByEmailAsync(email, now, ct);
        if (invitation is null) return null;

        var member = new StaffMember
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = invitation.RoleId,
            Role = invitation.Role,
            Status = StaffConstants.Statuses.Active,
            Source = StaffConstants.Sources.InvitationAccepted,
            InvitedBy = invitation.InvitedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        invitation.AcceptedAt = now;
        invitation.AcceptedUserId = user.Id;

        try
        {
            await _unitOfWork.StaffMemberRepository.AddAsync(member, ct);
            _unitOfWork.StaffInvitationRepository.Update(invitation);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Staff invitation {InvitationId} could not be accepted for {UserId}.", invitation.Id, user.Id);
            return null;
        }

        // Best-effort and AFTER the commit, unlike every admin-initiated staff write: the actor is
        // the invitee signing in, and refusing someone's sign-in because the audit store is down
        // would be the wrong trade. The invitation row itself records who accepted it and when.
        if (_audit is not null)
        {
            var recorded = await _audit.RecordSubjectAsync(new AdminAuditSubjectRecord(
                AdminAuditStaffActions.InvitationAccepted,
                AdminAuditEntityTypes.StaffMember,
                user.Id,
                user.Email,
                user.Id,
                "Accepted a staff invitation by signing in with the invited, verified address.",
                $"staff-invitation:{invitation.Id}",
                AfterSummary: new Dictionary<string, string?>
                {
                    ["role"] = invitation.Role?.Name,
                    ["invitation_id"] = invitation.Id.ToString(),
                }), ct);
            if (!recorded.IsSuccess)
            {
                _logger.LogError("Staff invitation {InvitationId} accepted but not audited: {Error}", invitation.Id, recorded.Error);
            }
        }

        return member;
    }

    private async Task TouchLastActiveAsync(StaffMember member, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (member.LastActiveAt is { } last && now - last < TimeSpan.FromMinutes(StaffConstants.LastActiveWriteIntervalMinutes))
        {
            return;
        }

        try
        {
            member.LastActiveAt = now;
            _unitOfWork.StaffMemberRepository.Update(member);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A convenience column; an access check must never fail because of it.
            _logger.LogWarning(ex, "Could not record staff activity for {UserId}.", member.UserId);
        }
    }
}

/// <summary>The auth service answers its own permission checks from its database, not over gRPC.</summary>
public sealed class DatabaseStaffAccessSource : IStaffAccessSource
{
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopes;

    public DatabaseStaffAccessSource(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<StaffAccess> GetAsync(Guid userId, CancellationToken ct = default)
    {
        // The resolver is a singleton; the unit of work is scoped.
        using var scope = _scopes.CreateScope();
        var service = (IStaffAccessService)scope.ServiceProvider.GetService(typeof(IStaffAccessService))!;
        return await service.GetAccessAsync(userId, ct);
    }
}

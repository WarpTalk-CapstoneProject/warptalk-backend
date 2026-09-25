using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Constants;
using WarpTalk.AuthService.Domain.Entities;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;

namespace WarpTalk.AuthService.Application.Services;

public interface IStaffAdminService
{
    Task<Result<StaffDirectoryPageDto>> ListStaffAsync(StaffDirectoryQuery query, CancellationToken ct = default);
    Task<Result<StaffMemberDto>> GetStaffAsync(Guid userId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<StaffInvitationDto>>> ListInvitationsAsync(CancellationToken ct = default);
    Task<Result<InviteStaffResultDto>> InviteAsync(AdminActorContext actor, InviteStaffRequest request, CancellationToken ct = default);
    Task<Result<StaffInvitationDto>> RevokeInvitationAsync(AdminActorContext actor, Guid invitationId, StaffActionRequest request, CancellationToken ct = default);
    Task<Result<StaffMemberDto>> ChangeRoleAsync(AdminActorContext actor, Guid userId, ChangeStaffRoleRequest request, CancellationToken ct = default);
    Task<Result<StaffMemberDto>> SuspendAsync(AdminActorContext actor, Guid userId, StaffActionRequest request, CancellationToken ct = default);
    Task<Result<StaffMemberDto>> ReactivateAsync(AdminActorContext actor, Guid userId, StaffActionRequest request, CancellationToken ct = default);
    Task<Result> RemoveAsync(AdminActorContext actor, Guid userId, StaffActionRequest request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<StaffRoleDto>>> ListRolesAsync(CancellationToken ct = default);
    Task<Result<StaffRoleDto>> GetRoleAsync(Guid roleId, CancellationToken ct = default);
    Task<Result<StaffRoleDto>> CreateRoleAsync(AdminActorContext actor, SaveStaffRoleRequest request, CancellationToken ct = default);
    Task<Result<StaffRoleDto>> UpdateRoleAsync(AdminActorContext actor, Guid roleId, SaveStaffRoleRequest request, CancellationToken ct = default);
    Task<Result<StaffRoleDto>> DuplicateRoleAsync(AdminActorContext actor, Guid roleId, DuplicateStaffRoleRequest request, CancellationToken ct = default);
    Task<Result> DeleteRoleAsync(AdminActorContext actor, Guid roleId, StaffActionRequest request, CancellationToken ct = default);

    Task<Result<StaffPermissionCatalogDto>> GetPermissionCatalogAsync(CancellationToken ct = default);
    Task<Result<PermissionHoldersDto>> GetPermissionHoldersAsync(string code, CancellationToken ct = default);

    Task<StaffSelfAccessDto> GetSelfAccessAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// /admin/staff and /admin/roles (G10).
///
/// Every write follows AdminUserService's ordering: change, save inside a transaction, record the
/// audit entry, and only then commit — a write the audit store refuses is rolled back and
/// reported. Every write re-reads the ACTOR's access from the database rather than trusting the
/// permission check that let the request in, because the guard rails (never beyond what you hold,
/// never your own access) need the actor's exact permissions, not a yes/no.
/// </summary>
public sealed class StaffAdminService : IStaffAdminService
{
    // A slug no role can have (slugs are [a-z0-9_]), for a name-only uniqueness check.
    private const string NoSlug = "~name-only~";

    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IStaffAccessService _access;
    private readonly IStaffAccessResolver _resolver;
    private readonly IAdminAuditRecorder _audit;
    private readonly IAuthEmailSender _email;
    private readonly TimeProvider _time;
    private readonly ILogger<StaffAdminService> _logger;

    public StaffAdminService(
        IUnitOfWork unitOfWork,
        IStaffAccessService access,
        IStaffAccessResolver resolver,
        IAdminAuditRecorder audit,
        IAuthEmailSender email,
        ILogger<StaffAdminService> logger,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _resolver = resolver;
        _audit = audit;
        _email = email;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Reads ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<StaffDirectoryPageDto>> ListStaffAsync(StaffDirectoryQuery query, CancellationToken ct = default)
    {
        var rows = await _unitOfWork.StaffMemberRepository.ListWithAccountsAsync(ct);
        var all = rows.Select(ToDto).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["all"] = all.Count,
            [StaffConstants.Statuses.Active] = all.Count(m => m.Status == StaffConstants.Statuses.Active),
            [StaffConstants.Statuses.Suspended] = all.Count(m => m.Status == StaffConstants.Statuses.Suspended),
        };

        IEnumerable<StaffMemberDto> filtered = all;
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var q = query.Q.Trim();
            filtered = filtered.Where(m =>
                m.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || m.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                || m.RoleName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            var roles = query.Role.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(m => roles.Contains(m.RoleSlug, StringComparer.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query.Status) && query.Status != "all")
        {
            filtered = filtered.Where(m => string.Equals(m.Status, query.Status, StringComparison.OrdinalIgnoreCase));
        }

        filtered = FilterLastActive(filtered, query.LastActive);
        filtered = Sort(filtered, query.Sort, string.Equals(query.Dir, "desc", StringComparison.OrdinalIgnoreCase));

        var list = filtered.ToList();
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var items = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Result.Success(new StaffDirectoryPageDto(items, list.Count, page, pageSize, counts));
    }

    public async Task<Result<StaffMemberDto>> GetStaffAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _unitOfWork.StaffMemberRepository.GetWithAccountAsync(userId, ct);
        return row is null
            ? Result.Failure<StaffMemberDto>("This person is not a staff member.", ErrorCodes.NotFound)
            : Result.Success(ToDto(row));
    }

    public async Task<Result<IReadOnlyList<StaffInvitationDto>>> ListInvitationsAsync(CancellationToken ct = default)
    {
        var invitations = await _unitOfWork.StaffInvitationRepository.ListAsync(ct);
        IReadOnlyList<StaffInvitationDto> dtos = invitations.Select(ToDto).ToList();
        return Result.Success(dtos);
    }

    public async Task<Result<IReadOnlyList<StaffRoleDto>>> ListRolesAsync(CancellationToken ct = default)
    {
        var roles = await _unitOfWork.RoleRepository.ListStaffRolesAsync(ct);
        var result = new List<StaffRoleDto>(roles.Count);
        foreach (var role in roles)
        {
            result.Add(await ToDtoAsync(role, ct));
        }

        return Result.Success<IReadOnlyList<StaffRoleDto>>(result);
    }

    public async Task<Result<StaffRoleDto>> GetRoleAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await _unitOfWork.RoleRepository.GetStaffRoleAsync(roleId, ct);
        return role is null
            ? Result.Failure<StaffRoleDto>("No such staff role.", ErrorCodes.NotFound)
            : Result.Success(await ToDtoAsync(role, ct));
    }

    public async Task<Result<StaffPermissionCatalogDto>> GetPermissionCatalogAsync(CancellationToken ct = default)
    {
        var roles = await _unitOfWork.RoleRepository.ListStaffRolesAsync(ct);
        var members = await _unitOfWork.StaffMemberRepository.ListWithAccountsAsync(ct);
        var roleCodes = roles.ToDictionary(r => r.Id, StaffAccessService.EffectiveCodes);

        var permissions = AdminPermissions.Definitions
            .Select(d => new StaffPermissionDto(
                d.Code,
                d.Area,
                d.Description,
                d.IsRead,
                roleCodes.Count(kv => kv.Value.Contains(d.Code)),
                members.Count(m => m.Member.Status == StaffConstants.Statuses.Active
                    && roleCodes.TryGetValue(m.Member.RoleId, out var codes) && codes.Contains(d.Code))))
            .ToList();

        return Result.Success(new StaffPermissionCatalogDto(AdminPermissions.Areas.Ordered, permissions));
    }

    public async Task<Result<PermissionHoldersDto>> GetPermissionHoldersAsync(string code, CancellationToken ct = default)
    {
        var definition = AdminPermissions.Find(code);
        if (definition is null)
            return Result.Failure<PermissionHoldersDto>("No such permission.", ErrorCodes.NotFound);

        var roles = (await _unitOfWork.RoleRepository.ListStaffRolesAsync(ct))
            .Where(r => StaffAccessService.EffectiveCodes(r).Contains(code))
            .ToList();
        var roleIds = roles.Select(r => r.Id).ToHashSet();
        var members = (await _unitOfWork.StaffMemberRepository.ListWithAccountsAsync(ct))
            .Where(m => roleIds.Contains(m.Member.RoleId))
            .Select(ToDto)
            .OrderBy(m => m.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dto = new StaffPermissionDto(
            definition.Code, definition.Area, definition.Description, definition.IsRead,
            roles.Count, members.Count(m => m.Status == StaffConstants.Statuses.Active));
        return Result.Success(new PermissionHoldersDto(
            dto,
            roles.Select(r => new StaffRoleRefDto(r.Id, r.Slug ?? string.Empty, r.Name, r.IsSystem)).ToList(),
            members));
    }

    public async Task<StaffSelfAccessDto> GetSelfAccessAsync(Guid userId, CancellationToken ct = default)
    {
        var access = await _resolver.GetAsync(userId, ct);
        return new StaffSelfAccessDto(
            access.IsStaff, access.RoleSlug, access.RoleName, access.IsSuperAdmin, access.EffectivePermissions());
    }

    // ── Staff writes ─────────────────────────────────────────────────────────────────────────

    public async Task<Result<InviteStaffResultDto>> InviteAsync(AdminActorContext actor, InviteStaffRequest request, CancellationToken ct = default)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (email.Length == 0 || email.Length > StaffConstants.EmailMaxLength || !EmailShape.IsMatch(email))
            return Result.Failure<InviteStaffResultDto>("Enter a valid email address.", ErrorCodes.ValidationError);
        var note = Trim(request.Reason);
        if (note is { Length: > StaffConstants.ReasonMaxLength })
            return Result.Failure<InviteStaffResultDto>("The note is too long.", ErrorCodes.ValidationError);

        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        var role = await _unitOfWork.RoleRepository.GetStaffRoleAsync(request.RoleId, ct);
        if (role is null) return Result.Failure<InviteStaffResultDto>("No such staff role.", ErrorCodes.NotFound);

        var grant = StaffGuards.CanGrant(actorAccess, IsSuperAdmin(role), StaffAccessService.EffectiveCodes(role));
        if (!grant.IsSuccess) return Fail<InviteStaffResultDto>(grant);

        var inviter = await _unitOfWork.UserRepository.GetByIdAsync(actor.ActorId, ct);
        var inviterName = inviter?.FullName ?? "A WarpTalk administrator";

        var user = await _unitOfWork.UserRepository.GetByEmailWithRolesAsync(email, ct);
        if (user is not null && user.DeletedAt is null)
        {
            var self = StaffGuards.NotSelf(actor.ActorId, user.Id);
            if (!self.IsSuccess) return Fail<InviteStaffResultDto>(self);
            if (await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(user.Id, ct) is not null)
                return Result.Failure<InviteStaffResultDto>("This person is already a staff member. Change their role instead.", ErrorCodes.Conflict);

            var member = new StaffMember
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleId = role.Id,
                Status = StaffConstants.Statuses.Active,
                Source = StaffConstants.Sources.Invited,
                InvitedBy = actor.ActorId,
                CreatedAt = Now,
                UpdatedAt = Now,
                UpdatedBy = actor.ActorId,
            };

            var written = await WriteAsync(
                async () => await _unitOfWork.StaffMemberRepository.AddAsync(member, ct),
                new AdminAuditSubjectRecord(
                    AdminAuditStaffActions.Granted, AdminAuditEntityTypes.StaffMember, user.Id, user.Email,
                    actor.ActorId, note ?? "Granted staff access.", actor.CorrelationId,
                    AfterSummary: new Dictionary<string, string?> { ["role"] = role.Name, ["status"] = member.Status }),
                ct);
            if (!written.IsSuccess) return Fail<InviteStaffResultDto>(written);

            _resolver.Invalidate(user.Id);
            var sent = await TrySendInvitationAsync(email, inviterName, role.Name, ct);
            var dto = await GetStaffAsync(user.Id, ct);
            return Result.Success(new InviteStaffResultDto("granted", dto.Value, null, sent));
        }

        var open = await _unitOfWork.StaffInvitationRepository.GetOpenByEmailAsync(email, ct);
        if (open is not null && open.ExpiresAt > Now)
            return Result.Failure<InviteStaffResultDto>("This address already has a pending invitation. Revoke it to send a different one.", ErrorCodes.Conflict);

        var invitation = new StaffInvitation
        {
            Id = Guid.NewGuid(),
            Email = email,
            RoleId = role.Id,
            Role = role,
            InvitedBy = actor.ActorId,
            Note = note,
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(StaffConstants.InvitationLifetimeDays),
        };

        var invited = await WriteAsync(
            async () =>
            {
                if (open is not null)
                {
                    // Expired but never closed: retire it so the one-open-invitation index admits the new one.
                    open.RevokedAt = Now;
                    open.RevokedBy = actor.ActorId;
                    open.RevokeReason = "Expired; replaced by a new invitation.";
                    _unitOfWork.StaffInvitationRepository.Update(open);
                    await _unitOfWork.SaveChangesAsync(ct);
                }

                await _unitOfWork.StaffInvitationRepository.AddAsync(invitation, ct);
            },
            new AdminAuditSubjectRecord(
                AdminAuditStaffActions.Invited, AdminAuditEntityTypes.StaffInvitation, invitation.Id, email,
                actor.ActorId, note ?? "Invited to staff.", actor.CorrelationId,
                AfterSummary: new Dictionary<string, string?>
                {
                    ["email"] = email,
                    ["role"] = role.Name,
                    ["expires_at"] = invitation.ExpiresAt.ToString("O"),
                }),
            ct);
        if (!invited.IsSuccess) return Fail<InviteStaffResultDto>(invited);

        var emailSent = await TrySendInvitationAsync(email, inviterName, role.Name, ct);
        return Result.Success(new InviteStaffResultDto("invited", null, ToDto(invitation), emailSent));
    }

    public async Task<Result<StaffInvitationDto>> RevokeInvitationAsync(AdminActorContext actor, Guid invitationId, StaffActionRequest request, CancellationToken ct = default)
    {
        var reason = RequireReason(request.Reason);
        if (!reason.IsSuccess) return Fail<StaffInvitationDto>(reason);

        var invitation = (await _unitOfWork.StaffInvitationRepository.ListAsync(ct)).FirstOrDefault(i => i.Id == invitationId);
        if (invitation is null) return Result.Failure<StaffInvitationDto>("No such invitation.", ErrorCodes.NotFound);
        if (StatusOf(invitation) != "pending")
            return Result.Failure<StaffInvitationDto>("Only a pending invitation can be revoked.", ErrorCodes.Conflict);

        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        var grant = StaffGuards.CanGrant(actorAccess, IsSuperAdmin(invitation.Role), StaffAccessService.EffectiveCodes(invitation.Role));
        if (!grant.IsSuccess) return Fail<StaffInvitationDto>(grant);

        var written = await WriteAsync(
            () =>
            {
                invitation.RevokedAt = Now;
                invitation.RevokedBy = actor.ActorId;
                invitation.RevokeReason = reason.Value;
                _unitOfWork.StaffInvitationRepository.Update(invitation);
                return Task.CompletedTask;
            },
            new AdminAuditSubjectRecord(
                AdminAuditStaffActions.InvitationRevoked, AdminAuditEntityTypes.StaffInvitation, invitation.Id,
                invitation.Email, actor.ActorId, reason.Value!, actor.CorrelationId,
                BeforeSummary: new Dictionary<string, string?> { ["status"] = "pending", ["role"] = invitation.Role.Name },
                AfterSummary: new Dictionary<string, string?> { ["status"] = "revoked" }),
            ct);
        return written.IsSuccess ? Result.Success(ToDto(invitation)) : Fail<StaffInvitationDto>(written);
    }

    public async Task<Result<StaffMemberDto>> ChangeRoleAsync(AdminActorContext actor, Guid userId, ChangeStaffRoleRequest request, CancellationToken ct = default)
    {
        var reason = RequireReason(request.Reason);
        if (!reason.IsSuccess) return Fail<StaffMemberDto>(reason);

        var context = await LoadTargetAsync(actor, userId, ct);
        if (!context.IsSuccess) return Fail<StaffMemberDto>(context);
        var (actorAccess, target) = context.Value!;

        var role = await _unitOfWork.RoleRepository.GetStaffRoleAsync(request.RoleId, ct);
        if (role is null) return Result.Failure<StaffMemberDto>("No such staff role.", ErrorCodes.NotFound);
        if (role.Id == target.RoleId) return Result.Failure<StaffMemberDto>("This person already holds that role.", ErrorCodes.Conflict);

        var grant = StaffGuards.CanGrant(actorAccess, IsSuperAdmin(role), StaffAccessService.EffectiveCodes(role));
        if (!grant.IsSuccess) return Fail<StaffMemberDto>(grant);

        var targetIsActiveSuper = target.Status == StaffConstants.Statuses.Active && IsSuperAdmin(target.Role);
        var keeps = StaffGuards.KeepsASuperAdmin(
            targetIsActiveSuper, IsSuperAdmin(role), await OtherActiveSuperAdminsAsync(userId, ct));
        if (!keeps.IsSuccess) return Fail<StaffMemberDto>(keeps);

        var before = target.Role.Name;
        var written = await WriteAsync(
            async () =>
            {
                target.RoleId = role.Id;
                target.Role = role;
                target.UpdatedAt = Now;
                target.UpdatedBy = actor.ActorId;
                _unitOfWork.StaffMemberRepository.Update(target);
                if (!IsSuperAdmin(role)) await _unitOfWork.StaffMemberRepository.RemoveLegacyAdminRoleAsync(userId, ct);
            },
            await MemberRecordAsync(AdminAuditStaffActions.RoleChanged, userId, actor, reason.Value!,
                new Dictionary<string, string?> { ["role"] = before },
                new Dictionary<string, string?> { ["role"] = role.Name }, ct),
            ct);
        if (!written.IsSuccess) return Fail<StaffMemberDto>(written);

        _resolver.Invalidate(userId);
        return await GetStaffAsync(userId, ct);
    }

    public Task<Result<StaffMemberDto>> SuspendAsync(AdminActorContext actor, Guid userId, StaffActionRequest request, CancellationToken ct = default) =>
        SetStatusAsync(actor, userId, request, StaffConstants.Statuses.Suspended, AdminAuditStaffActions.Suspended, ct);

    public Task<Result<StaffMemberDto>> ReactivateAsync(AdminActorContext actor, Guid userId, StaffActionRequest request, CancellationToken ct = default) =>
        SetStatusAsync(actor, userId, request, StaffConstants.Statuses.Active, AdminAuditStaffActions.Reactivated, ct);

    public async Task<Result> RemoveAsync(AdminActorContext actor, Guid userId, StaffActionRequest request, CancellationToken ct = default)
    {
        var reason = RequireReason(request.Reason);
        if (!reason.IsSuccess) return reason;

        var context = await LoadTargetAsync(actor, userId, ct);
        if (!context.IsSuccess) return context;
        var (_, target) = context.Value!;

        var targetIsActiveSuper = target.Status == StaffConstants.Statuses.Active && IsSuperAdmin(target.Role);
        var keeps = StaffGuards.KeepsASuperAdmin(targetIsActiveSuper, false, await OtherActiveSuperAdminsAsync(userId, ct));
        if (!keeps.IsSuccess) return keeps;

        var written = await WriteAsync(
            async () =>
            {
                _unitOfWork.StaffMemberRepository.Remove(target);
                await _unitOfWork.StaffMemberRepository.RemoveLegacyAdminRoleAsync(userId, ct);
            },
            await MemberRecordAsync(AdminAuditStaffActions.Removed, userId, actor, reason.Value!,
                new Dictionary<string, string?> { ["role"] = target.Role.Name, ["status"] = target.Status },
                new Dictionary<string, string?> { ["status"] = "removed" }, ct),
            ct);
        if (written.IsSuccess) _resolver.Invalidate(userId);
        return written;
    }

    private async Task<Result<StaffMemberDto>> SetStatusAsync(
        AdminActorContext actor, Guid userId, StaffActionRequest request, string status, string action, CancellationToken ct)
    {
        var reason = RequireReason(request.Reason);
        if (!reason.IsSuccess) return Fail<StaffMemberDto>(reason);

        var context = await LoadTargetAsync(actor, userId, ct);
        if (!context.IsSuccess) return Fail<StaffMemberDto>(context);
        var (_, target) = context.Value!;
        if (target.Status == status)
            return Result.Failure<StaffMemberDto>($"This staff member is already {status}.", ErrorCodes.Conflict);

        if (status == StaffConstants.Statuses.Suspended)
        {
            var keeps = StaffGuards.KeepsASuperAdmin(IsSuperAdmin(target.Role), false, await OtherActiveSuperAdminsAsync(userId, ct));
            if (!keeps.IsSuccess) return Fail<StaffMemberDto>(keeps);
        }

        var before = target.Status;
        var written = await WriteAsync(
            async () =>
            {
                target.Status = status;
                target.StatusReason = reason.Value;
                target.StatusChangedAt = Now;
                target.StatusChangedBy = actor.ActorId;
                target.UpdatedAt = Now;
                target.UpdatedBy = actor.ActorId;
                _unitOfWork.StaffMemberRepository.Update(target);
                if (status == StaffConstants.Statuses.Suspended)
                    await _unitOfWork.StaffMemberRepository.RemoveLegacyAdminRoleAsync(userId, ct);
            },
            await MemberRecordAsync(action, userId, actor, reason.Value!,
                new Dictionary<string, string?> { ["status"] = before },
                new Dictionary<string, string?> { ["status"] = status }, ct),
            ct);
        if (!written.IsSuccess) return Fail<StaffMemberDto>(written);

        _resolver.Invalidate(userId);
        return await GetStaffAsync(userId, ct);
    }

    // ── Role writes ──────────────────────────────────────────────────────────────────────────

    public async Task<Result<StaffRoleDto>> CreateRoleAsync(AdminActorContext actor, SaveStaffRoleRequest request, CancellationToken ct = default)
    {
        var validated = ValidateRoleFields(request);
        if (!validated.IsSuccess) return Fail<StaffRoleDto>(validated);
        var (name, description, codes) = validated.Value!;

        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        var grant = StaffGuards.CanGrant(actorAccess, false, codes);
        if (!grant.IsSuccess) return Fail<StaffRoleDto>(grant);

        return await InsertRoleAsync(actor, name, description, codes, AdminAuditStaffActions.RoleCreated,
            Trim(request.Reason) ?? "Created a staff role.", null, ct);
    }

    public async Task<Result<StaffRoleDto>> DuplicateRoleAsync(AdminActorContext actor, Guid roleId, DuplicateStaffRoleRequest request, CancellationToken ct = default)
    {
        var source = await _unitOfWork.RoleRepository.GetStaffRoleAsync(roleId, ct);
        if (source is null) return Result.Failure<StaffRoleDto>("No such staff role.", ErrorCodes.NotFound);

        var codes = StaffAccessService.EffectiveCodes(source);
        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        var grant = StaffGuards.CanGrant(actorAccess, false, codes);
        if (!grant.IsSuccess) return Fail<StaffRoleDto>(grant);

        var name = Trim(request.Name) ?? await FreeCopyNameAsync(source.Name, ct);
        if (name.Length > StaffConstants.RoleNameMaxLength)
            return Result.Failure<StaffRoleDto>("The name is too long.", ErrorCodes.ValidationError);

        return await InsertRoleAsync(actor, name, source.Description, codes, AdminAuditStaffActions.RoleDuplicated,
            $"Duplicated from {source.Name}.", source.Name, ct);
    }

    public async Task<Result<StaffRoleDto>> UpdateRoleAsync(AdminActorContext actor, Guid roleId, SaveStaffRoleRequest request, CancellationToken ct = default)
    {
        var validated = ValidateRoleFields(request);
        if (!validated.IsSuccess) return Fail<StaffRoleDto>(validated);
        var (name, description, codes) = validated.Value!;

        var role = await _unitOfWork.RoleRepository.GetStaffRoleAsync(roleId, ct);
        if (role is null) return Result.Failure<StaffRoleDto>("No such staff role.", ErrorCodes.NotFound);

        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        var actorMember = await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(actor.ActorId, ct);
        var editable = StaffGuards.CanEditRole(actorAccess, actorMember?.RoleId, role.Id, role.IsSystem, codes);
        if (!editable.IsSuccess) return Fail<StaffRoleDto>(editable);

        if (await _unitOfWork.RoleRepository.NameOrSlugTakenAsync(name, role.Slug ?? string.Empty, role.Id, ct))
            return Result.Failure<StaffRoleDto>("Another role already has that name.", ErrorCodes.Conflict);

        var beforeCodes = StaffAccessService.RoleCodes(role);
        var permissions = await _unitOfWork.PermissionRepository.GetByCodesAsync(codes.ToList(), ct);
        var before = new Dictionary<string, string?>
        {
            ["name"] = role.Name,
            ["permissions"] = string.Join(",", beforeCodes),
        };

        var written = await WriteAsync(
            () =>
            {
                role.Name = name;
                role.Description = description;
                role.UpdatedAt = Now;
                role.UpdatedBy = actor.ActorId;
                var keep = permissions.Select(p => p.Id).ToHashSet();
                var stale = role.RolePermissions.Where(rp => !keep.Contains(rp.PermissionId)).ToList();
                _unitOfWork.RoleRepository.RemovePermissions(stale);
                foreach (var row in stale)
                {
                    role.RolePermissions.Remove(row);
                }

                var have = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
                foreach (var permission in permissions.Where(p => !have.Contains(p.Id)))
                {
                    role.RolePermissions.Add(new RolePermission
                    {
                        RoleId = role.Id,
                        PermissionId = permission.Id,
                        Permission = permission,
                        CreatedAt = Now,
                        CreatedBy = actor.ActorId,
                    });
                }

                _unitOfWork.RoleRepository.Update(role);
                return Task.CompletedTask;
            },
            new AdminAuditSubjectRecord(
                AdminAuditStaffActions.RoleUpdated, AdminAuditEntityTypes.StaffRole, role.Id, name,
                actor.ActorId, Trim(request.Reason) ?? "Edited a staff role.", actor.CorrelationId,
                before,
                new Dictionary<string, string?> { ["name"] = name, ["permissions"] = string.Join(",", codes) }),
            ct);
        if (!written.IsSuccess) return Fail<StaffRoleDto>(written);

        // Every holder's permissions moved at once.
        _resolver.InvalidateAll();
        return await GetRoleAsync(role.Id, ct);
    }

    public async Task<Result> DeleteRoleAsync(AdminActorContext actor, Guid roleId, StaffActionRequest request, CancellationToken ct = default)
    {
        var reason = RequireReason(request.Reason);
        if (!reason.IsSuccess) return reason;

        var role = await _unitOfWork.RoleRepository.GetStaffRoleAsync(roleId, ct);
        if (role is null) return Result.Failure("No such staff role.", ErrorCodes.NotFound);
        if (role.IsSystem) return Result.Failure(StaffGuards.BuiltInRoleMessage, ErrorCodes.Forbidden);

        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        if (!actorAccess.Has(AdminPermissions.StaffManage)) return Result.Failure("Only staff managers can delete roles.", ErrorCodes.Forbidden);

        var members = await _unitOfWork.StaffMemberRepository.CountWithRoleAsync(role.Id, ct);
        var pending = await _unitOfWork.StaffInvitationRepository.CountPendingWithRoleAsync(role.Id, Now, ct);
        if (members > 0 || pending > 0)
        {
            return Result.Failure(
                $"This role is in use ({members} staff member(s), {pending} pending invitation(s)). Move them to another role first.",
                ErrorCodes.Conflict);
        }

        return await WriteAsync(
            () =>
            {
                // Soft delete: invitations accepted or revoked long ago still point at it, and the
                // audit log names it. The unique name/slug are freed so it can be recreated.
                role.DeletedAt = Now;
                role.DeletedBy = actor.ActorId;
                role.IsActive = false;
                // The original name is in the audit entry (label and before-summary).
                role.Name = $"deleted-{role.Id:N}";
                role.Slug = null;
                _unitOfWork.RoleRepository.RemovePermissions(role.RolePermissions.ToList());
                role.RolePermissions.Clear();
                _unitOfWork.RoleRepository.Update(role);
                return Task.CompletedTask;
            },
            new AdminAuditSubjectRecord(
                AdminAuditStaffActions.RoleDeleted, AdminAuditEntityTypes.StaffRole, role.Id, role.Name,
                actor.ActorId, reason.Value!, actor.CorrelationId,
                new Dictionary<string, string?> { ["name"] = role.Name, ["permissions"] = string.Join(",", StaffAccessService.RoleCodes(role)) }),
            ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task<Result<StaffRoleDto>> InsertRoleAsync(
        AdminActorContext actor, string name, string? description, IReadOnlyList<string> codes,
        string action, string reason, string? duplicatedFrom, CancellationToken ct)
    {
        var slug = await FreeSlugAsync(name, ct);
        if (await _unitOfWork.RoleRepository.NameOrSlugTakenAsync(name, slug, null, ct))
            return Result.Failure<StaffRoleDto>("Another role already has that name.", ErrorCodes.Conflict);

        var permissions = await _unitOfWork.PermissionRepository.GetByCodesAsync(codes.ToList(), ct);
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            Description = description,
            Scope = StaffConstants.RoleScopes.PlatformStaff,
            IsSystem = false,
            IsActive = true,
            CreatedAt = Now,
            CreatedBy = actor.ActorId,
            UpdatedAt = Now,
            UpdatedBy = actor.ActorId,
        };
        foreach (var permission in permissions)
        {
            role.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                PermissionId = permission.Id,
                Permission = permission,
                CreatedAt = Now,
                CreatedBy = actor.ActorId,
            });
        }

        var after = new Dictionary<string, string?> { ["name"] = name, ["permissions"] = string.Join(",", codes) };
        if (duplicatedFrom is not null) after["duplicated_from"] = duplicatedFrom;

        var written = await WriteAsync(
            async () => await _unitOfWork.RoleRepository.AddAsync(role, ct),
            new AdminAuditSubjectRecord(action, AdminAuditEntityTypes.StaffRole, role.Id, name,
                actor.ActorId, reason, actor.CorrelationId, AfterSummary: after),
            ct);
        return written.IsSuccess ? await GetRoleAsync(role.Id, ct) : Fail<StaffRoleDto>(written);
    }

    /// <summary>The actor's access and the target's row, with the self and hierarchy guards applied.</summary>
    private async Task<Result<(StaffAccess Actor, StaffMember Target)>> LoadTargetAsync(
        AdminActorContext actor, Guid userId, CancellationToken ct)
    {
        var self = StaffGuards.NotSelf(actor.ActorId, userId);
        if (!self.IsSuccess) return Fail<(StaffAccess, StaffMember)>(self);

        var target = await _unitOfWork.StaffMemberRepository.GetByUserIdAsync(userId, ct);
        if (target is null) return Result.Failure<(StaffAccess, StaffMember)>("This person is not a staff member.", ErrorCodes.NotFound);

        var actorAccess = await _access.GetAccessAsync(actor.ActorId, ct);
        var manage = StaffGuards.CanManage(actorAccess, IsSuperAdmin(target.Role), StaffAccessService.EffectiveCodes(target.Role));
        return manage.IsSuccess
            ? Result.Success((actorAccess, target))
            : Fail<(StaffAccess, StaffMember)>(manage);
    }

    private Task<int> OtherActiveSuperAdminsAsync(Guid userId, CancellationToken ct) =>
        _unitOfWork.StaffMemberRepository.CountActiveWithRoleSlugAsync(BuiltInStaffRoles.SuperAdmin, userId, ct);

    private async Task<AdminAuditSubjectRecord> MemberRecordAsync(
        string action, Guid userId, AdminActorContext actor, string reason,
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after, CancellationToken ct)
    {
        var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        return new AdminAuditSubjectRecord(action, AdminAuditEntityTypes.StaffMember, userId, user?.Email,
            actor.ActorId, reason, actor.CorrelationId, before, after);
    }

    /// <summary>Mutate, save, audit, commit — or roll back when the audit store refuses.</summary>
    private async Task<Result> WriteAsync(Func<Task> mutate, AdminAuditSubjectRecord record, CancellationToken ct)
    {
        await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            await mutate();
            await _unitOfWork.SaveChangesAsync(ct);

            var recorded = await _audit.RecordSubjectAsync(record, ct);
            if (!recorded.IsSuccess)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                _logger.LogWarning("Staff change abandoned because it could not be audited. Action: {Action}", record.Action);
                return Result.Failure(
                    recorded.Error ?? "The change was not made because it could not be audited.",
                    recorded.ErrorCode ?? ErrorCodes.InternalServerError);
            }

            await _unitOfWork.CommitTransactionAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync(ct);
            _logger.LogError(ex, "Staff change failed. Action: {Action}", record.Action);
            return Result.Failure("An unexpected error occurred while saving the change.", ErrorCodes.InternalServerError);
        }
    }

    private async Task<bool> TrySendInvitationAsync(string email, string inviterName, string roleName, CancellationToken ct)
    {
        try
        {
            await _email.SendStaffInvitationEmailAsync(email, inviterName, roleName, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The access (or invitation) stands; the page says the email did not go out.
            _logger.LogWarning(ex, "Staff invitation email to {Email} failed.", email);
            return false;
        }
    }

    private static Result<(string Name, string? Description, IReadOnlyList<string> Codes)> ValidateRoleFields(SaveStaffRoleRequest request)
    {
        var name = Trim(request.Name);
        if (name is null) return Result.Failure<(string, string?, IReadOnlyList<string>)>("A role needs a name.", ErrorCodes.ValidationError);
        if (name.Length > StaffConstants.RoleNameMaxLength)
            return Result.Failure<(string, string?, IReadOnlyList<string>)>($"The name can be at most {StaffConstants.RoleNameMaxLength} characters.", ErrorCodes.ValidationError);
        var description = Trim(request.Description);
        if (description is { Length: > StaffConstants.RoleDescriptionMaxLength })
            return Result.Failure<(string, string?, IReadOnlyList<string>)>("The description is too long.", ErrorCodes.ValidationError);

        var codes = StaffGuards.NormalizePermissions(request.Permissions);
        if (!codes.IsSuccess) return Result.Failure<(string, string?, IReadOnlyList<string>)>(codes.Error!, codes.ErrorCode);
        if (codes.Value!.Count == 0)
            return Result.Failure<(string, string?, IReadOnlyList<string>)>("Choose at least one permission.", ErrorCodes.ValidationError);

        return Result.Success((name, description, codes.Value!));
    }

    private async Task<string> FreeSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = "custom_" + Slugify(name);
        baseSlug = Truncate(baseSlug, 50);
        var slug = baseSlug;
        for (var i = 2; await _unitOfWork.RoleRepository.GetStaffRoleBySlugAsync(slug, ct) is not null
             || BuiltInStaffRoles.Find(slug) is not null; i++)
        {
            slug = $"{baseSlug}_{i}";
        }

        return slug;
    }

    private async Task<string> FreeCopyNameAsync(string sourceName, CancellationToken ct)
    {
        var candidate = Truncate($"{sourceName} (copy)", StaffConstants.RoleNameMaxLength);
        for (var i = 2; await _unitOfWork.RoleRepository.NameOrSlugTakenAsync(candidate, NoSlug, null, ct); i++)
        {
            candidate = Truncate($"{sourceName} (copy {i})", StaffConstants.RoleNameMaxLength);
        }

        return candidate;
    }

    public static string Slugify(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) || c is '-' or '_' or '/' or '.') builder.Append('_');
        }

        var slug = Regex.Replace(builder.ToString(), "_+", "_").Trim('_');
        return slug.Length == 0 ? "role" : slug;
    }

    private static bool IsSuperAdmin(Role role) =>
        string.Equals(role.Slug, BuiltInStaffRoles.SuperAdmin, StringComparison.Ordinal);

    private static Result<string> RequireReason(string? reason)
    {
        var trimmed = Trim(reason);
        if (trimmed is null) return Result.Failure<string>("A reason is required. It is the only record of why this was done.", ErrorCodes.ValidationError);
        if (trimmed.Length > StaffConstants.ReasonMaxLength)
            return Result.Failure<string>($"The reason can be at most {StaffConstants.ReasonMaxLength} characters.", ErrorCodes.ValidationError);
        return Result.Success(trimmed);
    }

    private static Result<T> Fail<T>(Result result) => Result.Failure<T>(result.Error ?? "Failed.", result.ErrorCode);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static IEnumerable<StaffMemberDto> FilterLastActive(IEnumerable<StaffMemberDto> items, string? filter)
    {
        var now = DateTime.UtcNow;
        return filter switch
        {
            "7d" => items.Where(m => m.LastActiveAt >= now.AddDays(-7)),
            "30d" => items.Where(m => m.LastActiveAt >= now.AddDays(-30)),
            "90d" => items.Where(m => m.LastActiveAt >= now.AddDays(-90)),
            "inactive30d" => items.Where(m => m.LastActiveAt is null || m.LastActiveAt < now.AddDays(-30)),
            "never" => items.Where(m => m.LastActiveAt is null),
            _ => items,
        };
    }

    private static IEnumerable<StaffMemberDto> Sort(IEnumerable<StaffMemberDto> items, string? sort, bool desc)
    {
        Func<StaffMemberDto, object?> key = sort switch
        {
            "email" => m => m.Email,
            "role" => m => m.RoleName,
            "status" => m => m.Status,
            "lastActive" => m => m.LastActiveAt,
            "lastSignIn" => m => m.LastSignInAt,
            "created" => m => m.CreatedAt,
            _ => m => m.FullName,
        };
        var comparer = Comparer<object?>.Create((a, b) => a switch
        {
            null when b is null => 0,
            null => -1,
            _ when b is null => 1,
            string sa when b is string sb => string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase),
            IComparable ca => ca.CompareTo(b),
            _ => 0,
        });
        return desc ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer);
    }

    private static StaffMemberDto ToDto(StaffMemberRow row)
    {
        var member = row.Member;
        return new StaffMemberDto(
            member.UserId,
            row.Email,
            row.FullName,
            row.AvatarUrl,
            member.RoleId,
            member.Role.Slug ?? string.Empty,
            member.Role.Name,
            IsSuperAdmin(member.Role),
            member.Status,
            member.StatusReason,
            member.StatusChangedAt,
            member.Source,
            member.InvitedBy,
            member.CreatedAt,
            member.LastActiveAt,
            row.LastLoginAt,
            row.AccountActive);
    }

    private StaffInvitationDto ToDto(StaffInvitation invitation) =>
        new(
            invitation.Id,
            invitation.Email,
            invitation.RoleId,
            invitation.Role?.Name ?? string.Empty,
            StatusOf(invitation),
            invitation.InvitedBy,
            invitation.Note,
            invitation.CreatedAt,
            invitation.ExpiresAt,
            invitation.AcceptedAt,
            invitation.RevokedAt,
            invitation.RevokeReason);

    private string StatusOf(StaffInvitation invitation) =>
        invitation.AcceptedAt is not null ? "accepted"
        : invitation.RevokedAt is not null ? "revoked"
        : invitation.ExpiresAt <= Now ? "expired"
        : "pending";

    private async Task<StaffRoleDto> ToDtoAsync(Role role, CancellationToken ct) =>
        new(
            role.Id,
            role.Slug ?? string.Empty,
            role.Name,
            role.Description,
            role.IsSystem,
            IsSuperAdmin(role),
            StaffAccessService.EffectiveCodes(role),
            await _unitOfWork.StaffMemberRepository.CountWithRoleAsync(role.Id, ct),
            await _unitOfWork.StaffInvitationRepository.CountPendingWithRoleAsync(role.Id, Now, ct),
            role.CreatedAt,
            role.UpdatedAt);
}

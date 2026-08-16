using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.AuthService.Application.Interfaces;
using WarpTalk.AuthService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;

namespace WarpTalk.AuthService.Application.Services;

/// <inheritdoc cref="IAdminUserService"/>
public class AdminUserService : IAdminUserService
{
    /// <summary>
    /// The statuses the directory accepts. An unknown one is rejected rather than ignored: a
    /// caller who filters on a typo and gets the unfiltered list back reads it as "these are all
    /// the locked accounts", which is the worst possible way to be wrong on this screen.
    /// </summary>
    private static readonly HashSet<string> Statuses =
        new(StringComparer.Ordinal) { "all", "active", "locked", "unverified", "deactivated", "deleted" };

    private static readonly string[] Sorts =
        ["created_desc", "created_asc", "name_asc", "name_desc", "last_login_desc", "last_login_asc"];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IAdminAuditRecorder _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdminUserService> _logger;

    public AdminUserService(
        IUnitOfWork unitOfWork,
        IAdminAuditRecorder audit,
        ILogger<AdminUserService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _audit = audit;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<AdminPagedResult<AdminUserSummaryDto>>> GetDirectoryAsync(
        AdminUserDirectoryQuery query,
        CancellationToken ct = default)
    {
        var status = Normalize(query.Status) ?? "all";
        if (!Statuses.Contains(status))
        {
            return Result.Failure<AdminPagedResult<AdminUserSummaryDto>>(
                $"Unknown status. Expected one of: {string.Join(", ", Statuses.OrderBy(s => s))}.",
                ErrorCodes.ValidationError);
        }

        if (!AdminSort.TryResolve(query.Sort, Sorts, "created_desc", out var sort))
        {
            return Result.Failure<AdminPagedResult<AdminUserSummaryDto>>(
                $"Unknown sort. Expected one of: {string.Join(", ", Sorts)}.",
                ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        try
        {
            var filter = new AdminUserDirectoryFilter(
                Search: Normalize(query.Search),
                Status: status,
                Role: Normalize(query.Role),
                Sort: sort);

            var (rows, total) = await _unitOfWork.UserRepository.GetDirectoryAsync(
                filter, page, pageSize, ct);

            return Result.Success(new AdminPagedResult<AdminUserSummaryDto>(
                rows.Select(AdminUserMapper.ToSummary).ToList(),
                page,
                pageSize,
                total));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin user directory read failed. Status: {Status}, Sort: {Sort}", status, sort);
            return Result.Failure<AdminPagedResult<AdminUserSummaryDto>>(
                "An unexpected error occurred while reading the user directory.",
                ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminUserDetailDto>> GetDetailAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var row = await _unitOfWork.UserRepository.GetDirectoryRowAsync(userId, ct);
            if (row is null)
            {
                return Result.Failure<AdminUserDetailDto>("No such user.", ErrorCodes.NotFound);
            }

            var sessions = await _unitOfWork.UserRepository.GetActiveSessionsAsync(userId, ct);
            return Result.Success(AdminUserMapper.ToDetail(row, sessions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin user detail read failed. UserId: {UserId}", userId);
            return Result.Failure<AdminUserDetailDto>(
                "An unexpected error occurred while reading the user.",
                ErrorCodes.InternalServerError);
        }
    }

    public Task<Result<AdminUserDetailDto>> RevokeSessionsAsync(
        Guid userId,
        AdminActorContext actor,
        AdminUserActionRequest request,
        CancellationToken ct = default)
        => PerformAsync(
            userId,
            actor,
            request,
            AdminAuditUserActions.SessionsRevoked,
            async (user, before) =>
            {
                await _unitOfWork.RefreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
                // The count is read BEFORE the revoke, in `before`. Reading it after would record
                // zero sessions ended on every entry.
                return new Dictionary<string, string?> { ["active_sessions"] = "0" };
            },
            ct);

    public Task<Result<AdminUserDetailDto>> SetAccountActiveAsync(
        Guid userId,
        bool isActive,
        AdminActorContext actor,
        AdminUserActionRequest request,
        CancellationToken ct = default)
        => PerformAsync(
            userId,
            actor,
            request,
            isActive ? AdminAuditUserActions.Reactivated : AdminAuditUserActions.Deactivated,
            async (user, before) =>
            {
                user.IsActive = isActive;
                user.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
                user.UpdatedBy = actor.ActorId;
                _unitOfWork.UserRepository.Update(user);

                var after = new Dictionary<string, string?>
                {
                    ["is_active"] = isActive.ToString(),
                };

                if (!isActive)
                {
                    // A deactivated account with live sessions is still a usable account until
                    // each refresh token happens to expire.
                    await _unitOfWork.RefreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
                    after["active_sessions"] = "0";
                }

                return after;
            },
            ct);

    public Task<Result<AdminUserDetailDto>> UnlockAsync(
        Guid userId,
        AdminActorContext actor,
        AdminUserActionRequest request,
        CancellationToken ct = default)
        => PerformAsync(
            userId,
            actor,
            request,
            AdminAuditUserActions.Unlocked,
            (user, before) =>
            {
                user.IsLocked = false;
                user.LockedUntil = null;
                // Cleared as well as unlocked. Leaving the counter at its limit would re-lock the
                // account on the next single mistyped password, which is not what "unlocked" says.
                user.FailedLoginAttempts = 0;
                user.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
                user.UpdatedBy = actor.ActorId;
                _unitOfWork.UserRepository.Update(user);

                return Task.FromResult<Dictionary<string, string?>>(new()
                {
                    ["is_locked"] = "False",
                    ["locked_until"] = null,
                    ["failed_login_attempts"] = "0",
                });
            },
            ct);

    /// <summary>
    /// The shape all three privileged actions share: change, record, and only then commit.
    ///
    /// The ordering is the point. `mutate` runs and is flushed inside a transaction, the audit
    /// entry goes to the workspace service, and the transaction is committed only once that
    /// entry is stored. A failure to record rolls the change back and reports it, so an
    /// unrecorded session revocation cannot happen — which is the reason these endpoints did not
    /// exist before there was a transport that could refuse.
    ///
    /// The audit call is made INSIDE the transaction rather than after committing, and it costs a
    /// network round-trip's worth of open transaction to do so. That is the price of the
    /// guarantee; these actions are rare and single-row.
    /// </summary>
    private async Task<Result<AdminUserDetailDto>> PerformAsync(
        Guid userId,
        AdminActorContext actor,
        AdminUserActionRequest request,
        string action,
        Func<Domain.Entities.User, IReadOnlyDictionary<string, string?>, Task<Dictionary<string, string?>>> mutate,
        CancellationToken ct)
    {
        var reason = Normalize(request?.Reason);
        if (reason is null)
        {
            return Result.Failure<AdminUserDetailDto>(
                "A reason is required. It is the only record of why this was done.",
                ErrorCodes.ValidationError);
        }

        var user = await _unitOfWork.UserRepository.GetByIdAsync(userId, ct);
        if (user is null || user.DeletedAt is not null)
        {
            return Result.Failure<AdminUserDetailDto>("No such user.", ErrorCodes.NotFound);
        }

        var sessionsBefore = await _unitOfWork.UserRepository.GetActiveSessionsAsync(userId, ct);
        var before = new Dictionary<string, string?>
        {
            ["is_active"] = user.IsActive.ToString(),
            ["is_locked"] = user.IsLocked.ToString(),
            ["active_sessions"] = sessionsBefore.Count.ToString(),
        };

        await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            var after = await mutate(user, before);
            await _unitOfWork.SaveChangesAsync(ct);

            var recorded = await _audit.RecordAsync(
                action,
                userId,
                actor.ActorId,
                reason,
                actor.CorrelationId,
                before,
                after,
                ct);

            if (!recorded.IsSuccess)
            {
                await _unitOfWork.RollbackTransactionAsync(ct);
                _logger.LogWarning(
                    "Admin user action abandoned because it could not be audited. Action: {Action}, UserId: {UserId}",
                    action,
                    userId);
                return Result.Failure<AdminUserDetailDto>(
                    recorded.Error ?? "The action was not performed because it could not be audited.",
                    recorded.ErrorCode ?? ErrorCodes.InternalServerError);
            }

            await _unitOfWork.CommitTransactionAsync(ct);
        }
        catch (Exception ex)
        {
            await _unitOfWork.RollbackTransactionAsync(ct);
            _logger.LogError(ex, "Admin user action failed. Action: {Action}, UserId: {UserId}", action, userId);
            return Result.Failure<AdminUserDetailDto>(
                "An unexpected error occurred while performing the action.",
                ErrorCodes.InternalServerError);
        }

        // Re-read rather than mapped from the entity in hand: the caller renders this straight
        // onto the row, and the derived status the directory shows is computed from columns the
        // mutation may have moved.
        return await GetDetailAsync(userId, ct);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Row-to-DTO, with the one derived value this feature owns: the account's status.
///
/// Kept out of the service so the precedence can be tested on its own — five booleans and two
/// nullable timestamps combine into more states than anyone holds in their head, and the order
/// they are checked in IS the behaviour.
/// </summary>
public static class AdminUserMapper
{
    /// <summary>
    /// Deleted beats locked beats deactivated beats unverified beats active.
    ///
    /// Deleted first because a deleted account is not "inactive", it is gone — reporting it as
    /// anything else invites an administrator to try to fix it. Locked before deactivated because
    /// a lockout is temporary and self-clearing while a deactivation is somebody's decision, and
    /// the temporary one is what an administrator is being asked about.
    /// </summary>
    public static string ToStatus(AdminUserDirectoryRow row, DateTime now)
    {
        if (row.DeletedAt != null) return "deleted";
        if (row.IsLocked || (row.LockedUntil != null && row.LockedUntil > now)) return "locked";
        if (!row.IsActive) return "deactivated";
        if (!row.EmailVerified) return "unverified";
        return "active";
    }

    public static AdminUserSummaryDto ToSummary(AdminUserDirectoryRow row)
        => new(
            row.Id,
            row.Email,
            row.FullName,
            row.AvatarUrl,
            ToStatus(row, DateTime.UtcNow),
            row.Roles,
            row.ActiveSessionCount,
            row.LastLoginAt,
            row.CreatedAt,
            row.DeletedAt);

    public static AdminUserDetailDto ToDetail(
        AdminUserDirectoryRow row,
        IReadOnlyList<AdminUserSessionRow> sessions)
    {
        var now = DateTime.UtcNow;
        return new AdminUserDetailDto(
            ToSummary(row),
            IsLockedOut: row.IsLocked || (row.LockedUntil != null && row.LockedUntil > now),
            row.LockedUntil,
            row.EmailVerified,
            row.IsActive,
            sessions
                .Select(s => new AdminUserSessionDto(s.Id, s.DeviceInfo, s.IpAddress, s.CreatedAt, s.ExpiresAt))
                .ToList());
    }
}

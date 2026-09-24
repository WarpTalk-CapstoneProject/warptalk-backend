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
    /// <summary>The largest roster one workspace sign-out takes in a single request.</summary>
    public const int MaxWorkspaceSignOutUsers = 500;

    /// <summary>What <c>usersByMonth</c> means at its edges, sent with it rather than implied.</summary>
    public const string UsersByMonthNote =
        "The latest month is month-to-date. Active = signed in or refreshed a session that month; totals exclude accounts deleted by the month's end.";

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

        var rangeError =
            AdminDateFilter.ValidateRange(query.CreatedFrom, query.CreatedTo, "createdFrom", "createdTo")
            ?? AdminDateFilter.ValidateRange(query.LastLoginFrom, query.LastLoginTo, "lastLoginFrom", "lastLoginTo");
        if (rangeError is not null)
        {
            return Result.Failure<AdminPagedResult<AdminUserSummaryDto>>(rangeError, ErrorCodes.ValidationError);
        }

        // Rejected rather than answered with an empty page: "never signed in, but last signed in
        // this week" is a contradiction, and an empty result reads as "nobody matches".
        if (query.NeverSignedIn == true && (query.LastLoginFrom is not null || query.LastLoginTo is not null))
        {
            return Result.Failure<AdminPagedResult<AdminUserSummaryDto>>(
                "neverSignedIn=true cannot be combined with lastLoginFrom or lastLoginTo.",
                ErrorCodes.ValidationError);
        }

        var (page, pageSize) = query.Normalize();

        try
        {
            var filter = new AdminUserDirectoryFilter(
                Search: Normalize(query.Search),
                Status: status,
                Role: Normalize(query.Role),
                Sort: sort,
                CreatedFrom: AdminDateFilter.ToUtc(query.CreatedFrom),
                CreatedTo: AdminDateFilter.ToUtc(query.CreatedTo),
                LastLoginFrom: AdminDateFilter.ToUtc(query.LastLoginFrom),
                LastLoginTo: AdminDateFilter.ToUtc(query.LastLoginTo),
                NeverSignedIn: query.NeverSignedIn);

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

    /// <summary>
    /// Metric definitions:
    /// <list type="bullet">
    /// <item><c>newUsers</c> — accounts whose <c>created_at</c> is in the window, including ones
    /// soft-deleted since, so a past period's figure never shrinks after the fact.</item>
    /// <item><c>activeUsers</c> — distinct accounts issued a refresh token in the window. Every
    /// sign-in (password or Google) and every access-token refresh inserts a
    /// <c>refresh_tokens</c> row, and rows are never deleted, so this is an event history.
    /// <c>users.last_login_at</c> was rejected: it keeps only the LATEST sign-in, so anyone who
    /// signed in during a past window and again later disappears from it.
    /// Limitation: someone whose whole visit fit inside an access token issued before the window
    /// (&lt; 30 minutes by default) is not counted in it, and an open tab that silently refreshes
    /// counts as active. There is no per-request activity log to do better from.</item>
    /// </list>
    /// </summary>
    public async Task<Result<AdminUserInsightsDto>> GetInsightsAsync(
        AdminInsightsQuery query,
        CancellationToken ct = default)
    {
        if (!AdminComparisonRange.TryResolve(query, out var window, out var error))
        {
            return Result.Failure<AdminUserInsightsDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var users = _unitOfWork.UserRepository;
            var tokens = _unitOfWork.RefreshTokenRepository;

            // Sequential: one DbContext cannot run two queries at once.
            var newUsers = await users.CountCreatedBetweenAsync(window.From, window.To, ct);
            var newUsersBefore = await users.CountCreatedBetweenAsync(window.PreviousFrom, window.PreviousTo, ct);
            var activeUsers = await tokens.CountDistinctUsersIssuedBetweenAsync(window.From, window.To, ct);
            var activeUsersBefore = await tokens.CountDistinctUsersIssuedBetweenAsync(
                window.PreviousFrom, window.PreviousTo, ct);
            // Bucketed on the local days of the request's tz, not UTC days: a Vietnam sign-up at
            // 23:30 local is 16:30Z, and a UTC bucket would file it under the right day only by luck.
            var days = window.Days();
            var counts = new int[days.Count];
            foreach (var createdAt in await users.GetCreatedAtBetweenAsync(window.From, window.To, ct))
            {
                var index = AdminComparisonRange.IndexOfDay(days, createdAt);
                if (index >= 0) counts[index]++;
            }

            var series = days
                .Select((day, i) => new AdminDailyCountDto(day.Key, counts[i]))
                .ToList();

            // WT-692: six local months of growth for the investor view on /admin/billing. Sequential
            // on purpose (one DbContext); 18 cheap indexed counts.
            var months = new List<AdminUserMonthDto>(AdminComparisonRange.GrowthMonths);
            foreach (var month in AdminComparisonRange.MonthsEnding(window.To, window.TimeZone))
            {
                months.Add(new AdminUserMonthDto(
                    month.Key,
                    await users.CountCreatedBetweenAsync(month.Start, month.End, ct),
                    await users.CountExistingAtAsync(month.End, ct),
                    await tokens.CountDistinctUsersIssuedBetweenAsync(month.Start, month.End, ct)));
            }

            return Result.Success(new AdminUserInsightsDto(
                window.Range,
                window.PreviousRange,
                [
                    new AdminInsightMetric("newUsers", newUsers, newUsersBefore, AdminInsightUnits.Count, true),
                    new AdminInsightMetric("activeUsers", activeUsers, activeUsersBefore, AdminInsightUnits.Count, true),
                ],
                series,
                months,
                UsersByMonthNote));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin user insights read failed.");
            return Result.Failure<AdminUserInsightsDto>(
                "An unexpected error occurred while reading user insights.",
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

    public async Task<Result<AdminWorkspaceSignOutResultDto>> RevokeSessionsForWorkspaceAsync(
        Guid workspaceId,
        AdminWorkspaceSignOutRequest request,
        AdminActorContext actor,
        CancellationToken ct = default)
    {
        if (Normalize(request?.Reason) is null)
        {
            return Result.Failure<AdminWorkspaceSignOutResultDto>(
                "A reason is required. It is the only record of why this was done.",
                ErrorCodes.ValidationError);
        }

        var userIds = (request!.UserIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (userIds.Count == 0)
        {
            return Result.Failure<AdminWorkspaceSignOutResultDto>(
                "Name at least one member to sign out.", ErrorCodes.ValidationError);
        }

        if (userIds.Count > MaxWorkspaceSignOutUsers)
        {
            return Result.Failure<AdminWorkspaceSignOutResultDto>(
                $"At most {MaxWorkspaceSignOutUsers} members can be signed out at once.", ErrorCodes.ValidationError);
        }

        // One account at a time, each through the same change-record-commit as the single
        // revoke: one account whose record fails is left untouched and reported, and never takes
        // the others down with it — nor goes unrecorded.
        var signedOut = new List<Guid>();
        var failed = new List<AdminWorkspaceSignOutFailureDto>();
        var perUser = new AdminUserActionRequest(request.Reason);
        foreach (var userId in userIds)
        {
            var result = await PerformAsync(
                userId,
                actor,
                perUser,
                AdminAuditUserActions.SessionsRevoked,
                async (user, before) =>
                {
                    await _unitOfWork.RefreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
                    return new Dictionary<string, string?> { ["active_sessions"] = "0" };
                },
                ct,
                workspaceId);

            if (result.IsSuccess)
            {
                signedOut.Add(userId);
            }
            else
            {
                failed.Add(new AdminWorkspaceSignOutFailureDto(userId, result.Error ?? "Not signed out."));
            }
        }

        return Result.Success(new AdminWorkspaceSignOutResultDto(workspaceId, signedOut, failed));
    }

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
        CancellationToken ct,
        Guid? workspaceId = null)
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
                workspaceId,
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

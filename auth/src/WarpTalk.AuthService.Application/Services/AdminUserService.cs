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
using WarpTalk.Shared.Contracts.Admin;

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
    private readonly ILogger<AdminUserService> _logger;

    public AdminUserService(IUnitOfWork unitOfWork, ILogger<AdminUserService> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
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

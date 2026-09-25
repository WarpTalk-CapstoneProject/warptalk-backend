using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// Read-only identity lookups consumed by other services over gRPC. The gRPC
/// boundary owns request parsing and response mapping only; every persistence
/// access behind these calls belongs here.
/// </summary>
public interface IUserDirectoryService
{
    Task<Result<UserIdentityDto>> GetUserByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// WT-699 / TC4104: one page of active user ids for a BROADCAST announcement. The page size is
    /// clamped to 1..<see cref="UserDirectoryService.MaxUserIdPageSize"/>; NextAfterId is null on
    /// the last page.
    /// </summary>
    Task<Result<(IReadOnlyList<Guid> UserIds, Guid? NextAfterId)>> ListActiveUserIdsAsync(
        Guid? afterId, int pageSize, CancellationToken ct = default);

    Task<Result<UserIdentityDto>> GetUserByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns the caller's language defaults, or a success with a null value when the
    /// user has no settings row yet — an absent row is a normal state, not a failure.
    /// </summary>
    Task<Result<UserLanguageDefaultsDto?>> GetLanguageDefaultsAsync(Guid userId, CancellationToken ct = default);

    Task<Result<RoleDto>> GetRoleByNameAsync(string name, CancellationToken ct = default);

    Task<Result<RoleDto>> GetRoleByIdAsync(Guid roleId, CancellationToken ct = default);
}

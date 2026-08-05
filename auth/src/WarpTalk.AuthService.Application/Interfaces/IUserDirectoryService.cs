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

    Task<Result<UserIdentityDto>> GetUserByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns the caller's language defaults, or a success with a null value when the
    /// user has no settings row yet — an absent row is a normal state, not a failure.
    /// </summary>
    Task<Result<UserLanguageDefaultsDto?>> GetLanguageDefaultsAsync(Guid userId, CancellationToken ct = default);

    Task<Result<RoleDto>> GetRoleByNameAsync(string name, CancellationToken ct = default);

    Task<Result<RoleDto>> GetRoleByIdAsync(Guid roleId, CancellationToken ct = default);
}

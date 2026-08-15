using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// The platform user directory, for the System Admin portal.
///
/// READ ONLY, and deliberately so on both counts.
///
/// Deleting an account is absent because a user's rows reach transcripts, voice profiles and
/// billing records across four services: removing one is a data-lifecycle decision, not a button
/// on a table.
///
/// Ending somebody's sessions is absent for a different and more specific reason. It is a
/// privileged action, so it must be audited — and the platform audit log is written by publishing
/// <c>admin.action_recorded</c> onto the bus, which the auth service does not have. Auth has no
/// MassTransit registration at all. Adding one puts a broker in the startup path of the service
/// every sign-in depends on, which is not a trade to make for an admin convenience without
/// deciding it on purpose. Session revocation therefore waits for that decision rather than
/// shipping unaudited.
/// </summary>
public interface IAdminUserService
{
    Task<Result<AdminPagedResult<AdminUserSummaryDto>>> GetDirectoryAsync(
        AdminUserDirectoryQuery query,
        CancellationToken ct = default);

    Task<Result<AdminUserDetailDto>> GetDetailAsync(Guid userId, CancellationToken ct = default);
}

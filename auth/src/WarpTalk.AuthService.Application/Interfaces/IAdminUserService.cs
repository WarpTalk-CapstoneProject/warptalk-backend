using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.AuthService.Application.Interfaces;

/// <summary>
/// The platform user directory, for the System Admin portal.
///
/// Three privileged actions, and no more. Each is reversible or self-limiting, and each is
/// recorded in the platform audit log BEFORE it is committed — see the note on ordering below.
///
/// Deleting an account is still absent, and for the original reason: a user's rows reach
/// transcripts, voice profiles and billing records across four services, so removing one is a
/// data-lifecycle decision rather than a button on a table.
///
/// On auditing. These actions waited on a decision, not on code. The platform audit log is
/// written by publishing <c>admin.action_recorded</c> onto the bus, and auth has no MassTransit
/// registration — adding one puts a broker in the startup path of the service every sign-in
/// depends on. The resolution was a third option: auth records the action synchronously over
/// gRPC, through <see cref="IAdminAuditRecorder"/>, into the same store the bus consumer writes
/// to. No new infrastructure, and — because it is synchronous — the action can be abandoned when
/// the record fails, which a publish could never offer.
///
/// So the ordering in every implementation here is: open a transaction, make the change, record
/// it, and commit ONLY if the record succeeded. The residual risk is an entry for a change whose
/// commit then failed — an over-record, which is the safe direction. The alternative ordering
/// risks the change without the entry, which is the outcome this feature existed to prevent.
/// </summary>
public interface IAdminUserService
{
    Task<Result<AdminPagedResult<AdminUserSummaryDto>>> GetDirectoryAsync(
        AdminUserDirectoryQuery query,
        CancellationToken ct = default);

    Task<Result<AdminUserDetailDto>> GetDetailAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Ends every session the account has open, by revoking its live refresh tokens.
    ///
    /// Does not lock the account or change its password — the person can sign in again
    /// immediately. This is the "signed in somewhere they should not be" response, not a
    /// punishment, and conflating the two would make it unusable for the case it exists for.
    /// </summary>
    Task<Result<AdminUserDetailDto>> RevokeSessionsAsync(
        Guid userId,
        AdminActorContext actor,
        AdminUserActionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Turns the account off, or back on. Nothing is deleted; the person cannot sign in.
    ///
    /// Deactivating also ends the sessions already open. Leaving them running would mean the
    /// account stays usable until each token happens to expire, which is not what anybody reading
    /// "deactivated" on the screen would expect.
    /// </summary>
    Task<Result<AdminUserDetailDto>> SetAccountActiveAsync(
        Guid userId,
        bool isActive,
        AdminActorContext actor,
        AdminUserActionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Clears a failed-login lockout so the person can try again now.
    ///
    /// The lockout clears itself when its window passes, so this only ever shortens a wait — which
    /// is why it is the mildest of the three and still audited like the others.
    /// </summary>
    Task<Result<AdminUserDetailDto>> UnlockAsync(
        Guid userId,
        AdminActorContext actor,
        AdminUserActionRequest request,
        CancellationToken ct = default);
}

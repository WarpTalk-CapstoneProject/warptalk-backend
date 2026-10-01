using WarpTalk.AuthService.Domain.Entities;

namespace WarpTalk.AuthService.Domain.Interfaces;

public interface IStaffInvitationRepository : IGenericRepository<StaffInvitation>
{
    /// <summary>The live (not accepted, not revoked, not expired) invitation for an address.</summary>
    Task<StaffInvitation?> GetPendingByEmailAsync(string normalizedEmail, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// The open (not accepted, not revoked) invitation for an address, expired or not — the one
    /// the unique index staff_invitations_open_email_key counts.
    /// </summary>
    Task<StaffInvitation?> GetOpenByEmailAsync(string normalizedEmail, CancellationToken ct = default);

    /// <summary>Every invitation, newest first, with its role loaded.</summary>
    Task<IReadOnlyList<StaffInvitation>> ListAsync(CancellationToken ct = default);

    Task<int> CountPendingWithRoleAsync(Guid roleId, DateTime now, CancellationToken ct = default);
}

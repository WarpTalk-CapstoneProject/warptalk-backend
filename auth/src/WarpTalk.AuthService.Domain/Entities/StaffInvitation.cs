using System;

namespace WarpTalk.AuthService.Domain.Entities;

/// <summary>
/// Staff access offered to an email address that has no WarpTalk account yet (G10).
///
/// Bound to the ADDRESS, not to a link: it is accepted the first time an account with that email,
/// VERIFIED, signs in. No token travels in the email, so a forwarded message grants nothing — the
/// recipient still has to prove they own the address.
/// </summary>
public class StaffInvitation
{
    public Guid Id { get; set; }

    /// <summary>Lower-cased and trimmed.</summary>
    public string Email { get; set; } = null!;

    public Guid RoleId { get; set; }

    public Guid InvitedBy { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public Guid? AcceptedUserId { get; set; }

    public DateTime? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public string? RevokeReason { get; set; }

    public virtual Role Role { get; set; } = null!;
}

using System;

namespace WarpTalk.AuthService.Domain.Entities;

/// <summary>
/// A platform account that works on the WarpTalk platform itself (G10). One row per person: the
/// staff role they hold and whether that access is live. Removing staff access deletes the row —
/// the audit log keeps the history, and the account itself is untouched.
/// </summary>
public class StaffMember
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid RoleId { get; set; }

    /// <summary><c>active</c> or <c>suspended</c> — see <see cref="Constants.StaffConstants.Statuses"/>.</summary>
    public string Status { get; set; } = null!;

    public string? StatusReason { get; set; }

    public DateTime? StatusChangedAt { get; set; }

    public Guid? StatusChangedBy { get; set; }

    /// <summary>How the row came to exist: migrated, invited, invitation_accepted, legacy_bridge.</summary>
    public string Source { get; set; } = null!;

    public Guid? InvitedBy { get; set; }

    /// <summary>The last time an admin request by this person reached the auth service's access check.</summary>
    public DateTime? LastActiveAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Role Role { get; set; } = null!;
}

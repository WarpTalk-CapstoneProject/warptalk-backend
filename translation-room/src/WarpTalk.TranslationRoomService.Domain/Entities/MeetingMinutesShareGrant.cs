using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// One person invited by email to read a room's biên bản.
///
/// Keyed on email rather than a user id because you invite the person, not their account: the
/// partner you send minutes to may not have signed up yet. The room's own read gate matches
/// invitations on email for the same reason.
/// </summary>
public class MeetingMinutesShareGrant
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>Lower-cased on write: "Nhi@" and "nhi@" are one person.</summary>
    public string Email { get; set; } = null!;

    /// <summary>Who let this person in — the question every sharing feature is eventually asked.</summary>
    public Guid? GrantedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}

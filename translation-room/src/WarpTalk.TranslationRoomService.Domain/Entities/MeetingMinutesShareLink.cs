using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// The sharing state of a room's biên bản: one link, its mode, and whether it is still live.
///
/// One row per room rather than one per version. A link sent last week must still open the
/// document after the secretary issues a revision — which version it lands on is
/// <c>meeting_minutes.is_current</c>, not a decision frozen into the URL.
/// </summary>
public class MeetingMinutesShareLink
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    /// <summary>External AuthService workspace id. No physical FK.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// URL-safe random secret. Unguessability is the whole security of a public link, so it is
    /// never derived from the room id or the minutes number, and it is rotated on revoke.
    /// </summary>
    public string Token { get; set; } = null!;

    /// <summary>
    /// <c>INVITED_ONLY</c> or <c>ANYONE_WITH_LINK</c> — see
    /// <see cref="Constants.MeetingMinutesConstants.ShareModeInvitedOnly"/>. A string rather than
    /// an enum because it is a column a human reads in psql when asked "was this public?".
    /// </summary>
    public string AccessMode { get; set; } = null!;

    /// <summary>
    /// Whether a viewer may take the file away. Off removes the button; it is a statement of
    /// intent, not a technical guarantee that a reader cannot copy what they can read.
    /// </summary>
    public bool AllowDownload { get; set; } = true;

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    /// <summary>
    /// Whether the link opens at all right now. Revocation and expiry are separate facts — one is
    /// somebody's decision, the other is the clock — and both close the same door.
    /// </summary>
    public bool IsLive(DateTime now) =>
        RevokedAt == null && (ExpiresAt == null || ExpiresAt > now);
}

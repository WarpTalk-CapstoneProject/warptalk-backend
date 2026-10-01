using System;

namespace WarpTalk.TranslationRoomService.Domain.Constants;

/// <summary>
/// One shared EXTERNAL_BRIDGE room per Google Meet code, with one capturer.
///
/// Every WarpTalk user in the same Meet call lands in the same room as their own participant with
/// their own mic. Exactly one of their desktops — the capturer — publishes the far side's mixed
/// audio as the stand-in identity; everyone else publishes only their own mic. The capturer holds
/// a lease it renews by heartbeat; when the lease goes stale any participant may take over.
/// </summary>
public static class BridgeRoomConstants
{
    /// <summary>The title a claim-created room gets — the same one the web's auto-room used.</summary>
    public const string DefaultTitle = "Google Meet call";

    /// <summary>
    /// Statuses that end a room's claim on its Meet code. Must match the predicate of
    /// translation_rooms_open_bridge_meet_code_key (migration 20261001090000). FAILED is included
    /// on top of <see cref="TranslationRoomConstants.TerminalStatuses"/> because a failed room must
    /// not block the next call on the same, reusable, Meet code.
    /// </summary>
    public static readonly string[] ClosedStatuses = ["ENDED", "CANCELLED", "EXPIRED", "FAILED"];

    /// <summary>A capturer whose last heartbeat is older than this is gone.</summary>
    public static readonly TimeSpan CapturerStaleAfter = TimeSpan.FromSeconds(45);

    /// <summary>How often a capturer is expected to heartbeat. A third of the lease.</summary>
    public static readonly TimeSpan CapturerHeartbeatInterval = TimeSpan.FromSeconds(15);

    public const string RoleCapturer = "capturer";
    public const string RoleMember = "member";

    public const string ErrorInvalidMeetCode = "A valid Google Meet code (xxx-xxxx-xxx) is required.";
    public const string ErrorNotWorkspaceMember = "You are not a member of this workspace.";
    public const string ErrorNotABridgeRoom = "This meeting does not bridge an external call.";
    public const string ErrorNotCapturer = "Another participant is capturing this call's audio.";
    public const string ErrorCapturerStillLive = "Another participant is still capturing this call's audio.";
    public const string ErrorNotParticipant = "Only a participant of this meeting can capture its audio.";
    public const string ErrorRoomClosed = "This translation room has already ended or been cancelled.";
}

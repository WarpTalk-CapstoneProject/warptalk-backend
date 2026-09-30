using System;
using System.Text.Json;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-826: a meeting's record — transcript, AI summary and recording — is shared with the people
/// who took part when the meeting ends, unless the host turned that off.
/// </summary>
/// <remarks>
/// <para>
/// THE RULE, IN ONE PLACE. Three writers need it (room creation, the pre-meeting settings edit,
/// and the host's own Publish/Unpublish), and two readers act on it (ending the room, and a
/// recording row that lands after the room ended). Stating it once is what keeps "share
/// automatically" from meaning three slightly different things.
/// </para>
/// <para>
/// WHY A SEPARATE FLAG AND NOT JUST <c>artifact_access</c>. Every room ever created has
/// <c>artifact_access</c> written into its blob — <c>TranslationRoomMapper.ResolveSettings</c>
/// serialises the whole object, so HOST_ONLY was stored as a default, not as a choice. A stored
/// HOST_ONLY therefore cannot tell "the host decided this is private" from "nobody decided
/// anything". <c>auto_share_record</c> is only ever written by a person (or by this rule at the
/// moment it acts), so its absence genuinely means "not stated".
/// </para>
/// <para>
/// EXISTING ROOMS. Absent reads as ON, so a room created before this flag that ENDS from now on is
/// published at the end, like a new room. A room that had already ended is never touched here —
/// nothing re-runs the end, and the recording hold below only lifts for a room this rule actually
/// published (the flag is resolved to TRUE at that moment, and older rooms never carry it).
/// </para>
/// </remarks>
public static class RecordAutoShare
{
    /// <summary>The toggle as the room holds it. Absent means on.</summary>
    public static bool IsOn(TranslationRoomSettings settings) => settings.AutoShareRecord ?? true;

    /// <summary>The level a room should carry while it has not ended, given its toggle.</summary>
    public static string ArtifactAccessFor(bool autoShare) =>
        autoShare ? ArtifactAccessLevels.AllParticipants : ArtifactAccessLevels.HostOnly;

    /// <summary>
    /// Publishes the record if the room's toggle says so. Called once, as the room ends.
    /// Returns whether it published.
    /// </summary>
    /// <remarks>
    /// The flag is resolved to an explicit TRUE here, not left absent: after this moment it is
    /// the durable record that THIS rule opened the meeting up, which is what
    /// <see cref="ReleasesRecordingHold"/> reads for a recording that arrives later.
    /// </remarks>
    public static bool ApplyAtMeetingEnd(TranslationRoomSettings settings)
    {
        if (!IsOn(settings))
            return false;

        settings.AutoShareRecord = true;
        settings.ArtifactAccess = ArtifactAccessLevels.AllParticipants;
        return true;
    }

    /// <summary>
    /// Whether auto-publishing counts as the host releasing the recording consent hold.
    /// </summary>
    /// <remarks>
    /// ENDED and an explicit TRUE, both. A room still running has not been published yet, and a
    /// room that ended before WT-826 has no flag at all — its recordings stay held until the host
    /// releases them by hand, exactly as they were.
    /// </remarks>
    public static bool ReleasesRecordingHold(TranslationRoom room, TranslationRoomSettings settings) =>
        string.Equals(room.Status, "ENDED", StringComparison.Ordinal)
        && settings.AutoShareRecord == true;

    /// <summary>
    /// A room's settings blob, or an empty one. Unreadable JSON yields defaults rather than
    /// throwing — the flag then reads as absent, which is ON, and the artifact level falls back to
    /// HOST_ONLY exactly as <see cref="ArtifactAccessHelper"/> reads it.
    /// </summary>
    public static TranslationRoomSettings Read(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return new TranslationRoomSettings();

        try
        {
            return JsonSerializer.Deserialize<TranslationRoomSettings>(
                       settingsJson,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new TranslationRoomSettings();
        }
        catch (JsonException)
        {
            return new TranslationRoomSettings();
        }
    }
}

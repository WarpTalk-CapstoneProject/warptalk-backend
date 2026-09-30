using System;
using System.Linq;
using System.Text.Json;
using WarpTalk.TranslationRoomService.Domain.Authorization;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// "Who may reach this room's artifacts" — the host always, and anyone who took part in or was
/// invited to the room when its <c>ArtifactAccess</c> policy says so.
/// </summary>
/// <remarks>
/// This is a stricter question than "who may read this room" (<c>RoomReadAccess</c>). Before
/// WT-849, artifacts denied invited-but-absent users even when the host had opened the room to
/// ALL_PARTICIPANTS — a standing invitation was treated as no relation at all, while the room page
/// itself admitted the same person. The fix: an ACCEPTED or PENDING invitation is treated as
/// participation for artifact purposes whenever the host has enabled ALL_PARTICIPANTS.
///
/// Share-link guests (people who were never in the invitation list) remain fully excluded;
/// this change only applies to users already named in TranslationRoomInvitations.
///
/// Every path that returns artifact bodies asks this type, never the room-read predicate.
/// </remarks>
public static class ArtifactAccessHelper
{
    /// <summary>
    /// For callers holding a room whose <c>TranslationRoomParticipants</c> and
    /// <c>TranslationRoomInvitations</c> navigations are loaded.
    /// </summary>
    public static bool HasAccessToRoomArtifacts(TranslationRoom room, Guid userId, string? userEmail = null)
        => HasAccessToRoomArtifacts(
            room.HostId,
            room.Settings,
            IsParticipantOrInvited(room, userId, userEmail),
            userId);

    /// <summary>
    /// The relation half of the gate on its own, so a caller that has to say WHY it refused asks
    /// the same question the gate asked — not a looser copy that forgets the status allow-list or
    /// the email normalisation.
    /// </summary>
    public static bool IsParticipantOrInvited(TranslationRoom room, Guid userId, string? userEmail)
    {
        if (room.TranslationRoomParticipants.Any(p => p.UserId == userId)) return true;

        var normalizedEmail = RoomReadAccess.NormalizeEmail(userEmail);
        return normalizedEmail is not null &&
            room.TranslationRoomInvitations.Any(i =>
                RoomReadAccess.NormalizeEmail(i.Email) == normalizedEmail &&
                RoomReadAccess.InvitationStatusesGrantingRead.Contains(i.Status));
    }

    /// <summary>
    /// For callers that have already resolved participation and invitation status elsewhere — the
    /// room-history query materialises its roster in one batch across every room on the page
    /// rather than loading the navigation per room, so it can answer
    /// <paramref name="isParticipantOrInvited"/> without a second round trip. Splitting the
    /// decision out this way is what lets the list projection ask the same question the download
    /// endpoint asks, instead of restating a looser one.
    /// </summary>
    public static bool HasAccessToRoomArtifacts(Guid hostId, string? settingsJson, bool isParticipantOrInvited, Guid userId)
    {
        if (hostId == userId) return true;
        if (!isParticipantOrInvited) return false;

        return ReadArtifactAccessLevel(settingsJson) == ArtifactAccessLevels.AllParticipants;
    }

    /// <summary>
    /// Why this caller was refused, in words for the person who was refused.
    ///
    /// Room artifacts default to HOST_ONLY, so the common denial is a person who has a relation
    /// to the meeting reading outputs the host has not shared — and telling them they are
    /// "unauthorized" reads as a bug rather than as a setting the host can change. The two cases
    /// need different sentences because they need different next actions: one is "ask the host",
    /// the other is "you have no relation to this meeting".
    ///
    /// WT-849: an invitee now counts as having a relation, so an invited-but-absent reader of a
    /// HOST_ONLY room is told to ask the host, the same as someone who attended.
    /// </summary>
    public static string DescribeArtifactDenial(bool isParticipantOrInvited) =>
        isParticipantOrInvited
            ? "The host has not shared this meeting's outputs. Only the host can read them until they change who this meeting is shared with."
            : "This meeting's outputs are only available to the people who took part in or were invited to it.";

    /// <summary>
    /// Anything unreadable or unrecognised resolves to <see cref="ArtifactAccessLevels.HostOnly"/>.
    /// Malformed settings JSON used to escape this helper as an unhandled exception; a room whose
    /// blob cannot be parsed now denies non-hosts rather than 500-ing, which is the direction an
    /// authorization check should fail in.
    /// </summary>
    private static string ReadArtifactAccessLevel(string? settingsJson)
    {
        if (string.IsNullOrEmpty(settingsJson))
            return ArtifactAccessLevels.HostOnly;

        TranslationRoomSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<TranslationRoomSettings>(
                settingsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return ArtifactAccessLevels.HostOnly;
        }

        var level = settings?.ArtifactAccess;
        return ArtifactAccessLevels.IsValid(level) ? level! : ArtifactAccessLevels.HostOnly;
    }
}

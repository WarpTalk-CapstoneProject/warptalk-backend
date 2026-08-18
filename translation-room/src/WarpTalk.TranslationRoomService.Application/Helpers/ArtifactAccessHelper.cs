using System;
using System.Linq;
using System.Text.Json;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.ValueObjects;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// "Who may reach this room's artifacts" — the host always, and anyone who took part in the room
/// when its <c>ArtifactAccess</c> policy says so.
/// </summary>
/// <remarks>
/// This is a stricter question than "who may read this room" (<c>RoomReadAccess</c>). Room-read
/// admits an invited-by-email user who never joined; artifacts do not, and must not — a standing
/// invitation is not consent to read the recording or the AI summary of a meeting someone never
/// attended. Every path that returns artifact bodies asks this type, never the room-read predicate.
/// </remarks>
public static class ArtifactAccessHelper
{
    /// <summary>
    /// For callers holding a room whose <c>TranslationRoomParticipants</c> navigation is loaded.
    /// </summary>
    public static bool HasAccessToRoomArtifacts(TranslationRoom room, Guid userId)
        => HasAccessToRoomArtifacts(
            room.HostId,
            room.Settings,
            room.TranslationRoomParticipants.Any(p => p.UserId == userId),
            userId);

    /// <summary>
    /// For callers that have already resolved participation elsewhere — the room-history query
    /// materialises its roster in one batch across every room on the page rather than loading the
    /// navigation per room, so it can answer <paramref name="isParticipant"/> without a second
    /// round trip. Splitting the decision out this way is what lets the list projection ask the
    /// same question the download endpoint asks, instead of restating a looser one.
    /// </summary>
    public static bool HasAccessToRoomArtifacts(Guid hostId, string? settingsJson, bool isParticipant, Guid userId)
    {
        if (hostId == userId) return true;
        if (!isParticipant) return false;

        return ReadArtifactAccessLevel(settingsJson) == ArtifactAccessLevels.AllParticipants;
    }

    /// <summary>
    /// Why this caller was refused, in words for the person who was refused.
    ///
    /// "Unauthorized to download this artifact." was the whole answer, and it is the wrong
    /// sentence for the case that actually happens. Room artifacts default to HOST_ONLY, so the
    /// overwhelmingly common denial is a person who WAS in the meeting reading the summary of the
    /// meeting they attended — and telling them they are unauthorized reads as a bug in the
    /// product rather than as a setting the host can change. summary-absence.ts on the web side
    /// already drew this distinction for the Summary tab (SummaryAbsence.withheld); the download
    /// endpoint kept answering with the flat denial, so the same meeting said two different
    /// things depending on which control you touched.
    ///
    /// The two cases need different sentences because they need different next actions: one is
    /// "ask the host", the other is "you were not there".
    /// </summary>
    public static string DescribeArtifactDenial(bool isParticipant) =>
        isParticipant
            ? "The host has not shared this meeting's outputs. Only the host can read them until they change who this meeting is shared with."
            : "This meeting's outputs are only available to the people who took part in it.";

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

using System;

namespace WarpTalk.Shared;

/// <summary>
/// WT-525. The stand-in participant of an EXTERNAL_BRIDGE room — the seat that represents
/// everyone on the far side of a Google Meet call.
///
/// WHY THIS IS IN SHARED RATHER THAN IN ONE SERVICE
///   Three services have to agree on the exact same string or the bridge silently does the wrong
///   thing, and none of them can import the others' Domain:
///
///     translation-room  seeds the seat when the room is created, so the audio-route mesh has a
///                       second party to build routes between.
///     meeting           mints the LiveKit token for it (the one place a token is issued for an
///                       identity that is not the caller).
///     warptalk-ai       routes on it — stt_worker takes speaker_id straight from
///                       participant_identity, so this string IS the far side's identity to the
///                       whole pipeline.
///
///   Spelled out separately in each, a rename in one would not fail a build anywhere; it would
///   produce a meeting where the far side is transcribed as a stranger, or not at all.
///
/// The value is a fixed Guid rather than a per-room one on purpose: a room has at most one far
/// side, and a stable identity means the pipeline needs no special case to recognise it.
/// </summary>
public static class ExternalBridgeConstants
{
    /// <summary>The stand-in's user id, and therefore its LiveKit participant identity.</summary>
    public static readonly Guid ParticipantUserId = new("00000000-0000-0000-0000-00000000b21d");

    /// <summary>What the seat is called in the roster.</summary>
    public const string DisplayName = "External Meeting";

    /// <summary>The room type this seat may exist in — TranslationRoomTypes.ExternalBridge.</summary>
    public const string RoomType = "EXTERNAL_BRIDGE";

    /// <summary>
    /// True only for the exact stored type. Deliberately NOT a fuzzy match: the one caller that
    /// matters is an authorization gate deciding whether to mint a token for an identity other
    /// than the caller's, and a permissive comparison there is a hole rather than a convenience.
    /// </summary>
    public static bool IsBridgeRoomType(string? translationRoomType) =>
        string.Equals(translationRoomType, RoomType, StringComparison.OrdinalIgnoreCase);
}

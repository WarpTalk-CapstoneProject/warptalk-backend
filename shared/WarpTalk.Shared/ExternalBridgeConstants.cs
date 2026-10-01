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

    /// <summary>
    /// Whether <paramref name="callerUserId"/> owns a bridge room's far-side audio — may mint the
    /// stand-in's token and report the far side's speakers. (Saying what language the far side
    /// speaks moved to the wider <see cref="CanControlBridgeSession"/> on 2026-10-01.)
    ///
    /// One shared bridge room per Google Meet code means many WarpTalk users in one room, and only
    /// ONE of their desktops (the capturer) may publish as the stand-in; two would double the far
    /// side. So the owner is the room's current capturer. A room with no capturer — one created
    /// before bridge claim, or a response from an older server — falls back to the HOST, which is
    /// exactly the rule this replaced, never to "anyone".
    ///
    /// Spelled once here because Meeting (bridge token) and the Gateway (far-speaker hints) must
    /// give the same answer.
    /// </summary>
    public static bool IsBridgeAudioOwner(string? capturerUserId, string? hostId, string? callerUserId)
    {
        if (string.IsNullOrWhiteSpace(callerUserId))
        {
            return false;
        }

        var owner = string.IsNullOrWhiteSpace(capturerUserId) ? hostId : capturerUserId;
        return !string.IsNullOrWhiteSpace(owner)
            && string.Equals(owner, callerUserId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="callerUserId"/> is the CURRENT capturer of an EXTERNAL_BRIDGE
    /// room. Always false for any other room type, and for a bridge room with no capturer — the
    /// capturer clause never applies outside a bridge, whatever a stale column might say.
    /// </summary>
    public static bool IsCurrentBridgeCapturer(string? translationRoomType, string? capturerUserId, string? callerUserId)
        => IsBridgeRoomType(translationRoomType)
            && !string.IsNullOrWhiteSpace(capturerUserId)
            && !string.IsNullOrWhiteSpace(callerUserId)
            && string.Equals(capturerUserId, callerUserId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// PO rule 2026-10-01: who may drive a bridge room's SESSION controls — Start translation
    /// (/resume), Stop translation (/stop-translation), transcript Pause/Resume and "They speak"
    /// (SetExternalMeetingLanguage). The room host OR, in an EXTERNAL_BRIDGE room only, the current
    /// capturer. The web gates the same buttons with <c>canControlBridge</c> (warptalk-web#656).
    ///
    /// Why the capturer: after a takeover the person whose desktop is in the Meet call — and so
    /// the one watching the bridge popup — may not be the host; refusing them every control left
    /// a bridge nobody in the call could steer. Why still the host: the host who handed capture to
    /// someone else keeps the authority they always had (and, for "They speak", is also in the call).
    ///
    /// "Host" here is the identity half only: <paramref name="hostId"/> (the booker) and, when the
    /// caller's existing gate honoured a transfer, <paramref name="effectiveHostId"/>. Endpoints
    /// that additionally widen host authority (workspace Owner/Admin, participants-may-start) keep
    /// doing that on their own and OR it with this — this rule only ever ADDS the capturer.
    ///
    /// NOT the rule for the stand-in's LiveKit token: publishing the far side stays with
    /// <see cref="IsBridgeAudioOwner"/> (one capturer), because two publishers would double it.
    /// </summary>
    public static bool CanControlBridgeSession(
        string? translationRoomType,
        string? hostId,
        string? effectiveHostId,
        string? bridgeCapturerUserId,
        string? callerUserId)
    {
        if (string.IsNullOrWhiteSpace(callerUserId))
        {
            return false;
        }

        if (SameUser(hostId, callerUserId) || SameUser(effectiveHostId, callerUserId))
        {
            return true;
        }

        return IsCurrentBridgeCapturer(translationRoomType, bridgeCapturerUserId, callerUserId);
    }

    private static bool SameUser(string? a, string b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

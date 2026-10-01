using System;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Application.Authorization;

/// <summary>
/// PO rule 2026-10-01: in an EXTERNAL_BRIDGE room, the bridge session's controls — Start
/// translation (/resume) and Stop translation (/stop-translation) here — belong to the room host
/// OR the bridge's current capturer. The predicate itself is
/// <see cref="ExternalBridgeConstants.CanControlBridgeSession"/>, shared with TranscriptService
/// (transcript Pause/Resume) and the Gateway ("They speak") so all four answer alike.
///
/// This type only adapts the entity: each endpoint keeps its own host check unchanged
/// (<see cref="TranslationRoom.IsHostedBy"/>, or <see cref="RoomHostAccess"/> with its
/// Owner/Admin widening) and ORs this in, so a non-bridge room answers exactly as it did.
/// </summary>
public static class RoomBridgeControlAccess
{
    /// <summary>
    /// True only for the current capturer of an EXTERNAL_BRIDGE room. A bridge room with no
    /// capturer adds nobody (its host already passes the host check).
    /// </summary>
    public static bool IsCurrentBridgeCapturer(TranslationRoom room, Guid requestedByUserId)
        => ExternalBridgeConstants.IsCurrentBridgeCapturer(
            room.TranslationRoomType,
            room.BridgeCapturerUserId?.ToString(),
            requestedByUserId.ToString());

    /// <summary>
    /// Host (effective host, transfer-aware — the rule <see cref="TranslationRoom.IsHostedBy"/>
    /// already applied) OR the bridge capturer. For endpoints whose host gate was
    /// <c>IsHostedBy</c>; endpoints gated on <see cref="RoomHostAccess"/> OR
    /// <see cref="IsCurrentBridgeCapturer"/> into that instead.
    /// </summary>
    public static bool CanControlBridgeSession(TranslationRoom room, Guid requestedByUserId)
        => ExternalBridgeConstants.CanControlBridgeSession(
            room.TranslationRoomType,
            // The booker is deliberately NOT passed: IsHostedBy never let a booker who handed the
            // room over keep host controls, and this must not change that.
            hostId: null,
            effectiveHostId: room.EffectiveHostId.ToString(),
            room.BridgeCapturerUserId?.ToString(),
            requestedByUserId.ToString());
}

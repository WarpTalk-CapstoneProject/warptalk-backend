using System;
using System.Collections.Generic;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

/// <summary>
/// POST /api/v1/translation-rooms/bridge/claim — "put me in this Meet call's WarpTalk room".
///
/// <paramref name="MeetCode"/> may be the bare code (<c>abc-defg-hij</c>, any case) or a
/// meet.google.com link. The language fields are the same planning inputs the web's auto-room
/// sends to create; they are used to CREATE a room when none is open for the code, and
/// <paramref name="SourceLanguage"/> is also the caller's own speak/listen language when joining.
/// </summary>
public record ClaimBridgeRoomRequest(
    Guid? WorkspaceId,
    string? MeetCode,
    string? SourceLanguage = null,
    List<string>? TargetLanguages = null,
    string? ExternalMeetingLanguage = null,
    string? DisplayName = null);

/// <summary>
/// What claim answers. <paramref name="BridgeRole"/> is <c>"capturer"</c> when the caller's desktop
/// must publish the far side's audio (and request the bridge token), <c>"member"</c> when it must
/// publish only its own mic. A capturer renews its lease every
/// <paramref name="CapturerHeartbeatIntervalSeconds"/>; after <paramref name="CapturerLeaseSeconds"/>
/// without one, any member may take over.
/// </summary>
public record ClaimBridgeRoomResponse(
    TranslationRoomDto Room,
    TranslationRoomParticipantDto? Participant,
    string BridgeRole,
    bool Created,
    int CapturerHeartbeatIntervalSeconds,
    int CapturerLeaseSeconds);

/// <summary>Heartbeat / takeover answer.</summary>
public record BridgeCapturerStatusDto(
    Guid RoomId,
    string BridgeRole,
    Guid? CapturerUserId,
    DateTime? CapturerHeartbeatAt,
    int CapturerLeaseSeconds);

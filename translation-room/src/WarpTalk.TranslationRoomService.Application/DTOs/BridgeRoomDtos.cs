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
///
/// <paramref name="AudioMode"/> — <c>"voice"</c> or <c>"text"</c>, optional. Null keeps the
/// caller's current mode (voice for a first claim). Text = the caller uses their real mic and
/// speakers in Meet, so no dub of their speech is synthesized for the far side. A claim asking for
/// voice while the caller is already text-only and translation is running keeps text (claim never
/// fails over it); the effective mode is in the response.
/// </summary>
public record ClaimBridgeRoomRequest(
    Guid? WorkspaceId,
    string? MeetCode,
    string? SourceLanguage = null,
    List<string>? TargetLanguages = null,
    string? ExternalMeetingLanguage = null,
    string? DisplayName = null,
    string? AudioMode = null);

/// <summary>
/// What claim answers. <paramref name="BridgeRole"/> is <c>"capturer"</c> when the caller's desktop
/// must publish the far side's audio (and request the bridge token), <c>"member"</c> when it must
/// publish only its own mic. A capturer renews its lease every
/// <paramref name="CapturerHeartbeatIntervalSeconds"/>; after <paramref name="CapturerLeaseSeconds"/>
/// without one, any member may take over. <paramref name="AudioMode"/> is the caller's effective
/// bridge audio mode after the claim (<c>"voice"</c> / <c>"text"</c>).
/// </summary>
public record ClaimBridgeRoomResponse(
    TranslationRoomDto Room,
    TranslationRoomParticipantDto? Participant,
    string BridgeRole,
    bool Created,
    int CapturerHeartbeatIntervalSeconds,
    int CapturerLeaseSeconds,
    string AudioMode = "voice");

/// <summary>Heartbeat / takeover answer.</summary>
public record BridgeCapturerStatusDto(
    Guid RoomId,
    string BridgeRole,
    Guid? CapturerUserId,
    DateTime? CapturerHeartbeatAt,
    int CapturerLeaseSeconds);

/// <summary>PUT /api/v1/translation-rooms/{id}/bridge/audio-mode — <c>{ "mode": "voice" | "text" }</c>.</summary>
public record SetBridgeAudioModeRequest(string? Mode);

/// <summary>
/// The caller's bridge audio mode after the request. <paramref name="TranslationActive"/> says
/// whether a translation session is running, i.e. whether text → voice is currently locked.
/// </summary>
public record BridgeAudioModeDto(
    Guid RoomId,
    Guid UserId,
    string Mode,
    bool TranslationActive);

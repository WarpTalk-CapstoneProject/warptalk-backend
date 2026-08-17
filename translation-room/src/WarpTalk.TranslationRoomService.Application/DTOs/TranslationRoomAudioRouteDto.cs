using System;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Application.DTOs;

public record TranslationRoomAudioRouteDto(
    Guid Id,
    Guid TranslationRoomId,
    Guid SourceParticipantId,
    Guid TargetParticipantId,
    string SourceLanguage,
    string TargetLanguage,
    bool VoiceCloneEnabled,
    string? StreamId,
    string Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    DateTime CreatedAt,
    Guid? SourceUserId = null,
    Guid? TargetUserId = null,
    /// <summary>
    /// WT-396 — the voice this route's SPEAKER chose to be dubbed in, or null to clone them live
    /// from the meeting.
    ///
    /// Resolved when routes are published rather than stored on the route, because it is the
    /// person's current choice and not a property of the pairing: changing it takes effect on the
    /// next broadcast instead of needing every existing route rewritten.
    ///
    /// Rides here because this payload is already the only thing the AI workers learn about a
    /// room, and voice_clone_enabled — the field this one sits beside — took the same path for
    /// the same reason.
    /// </summary>
    string? SourceDubVoiceId = null
);

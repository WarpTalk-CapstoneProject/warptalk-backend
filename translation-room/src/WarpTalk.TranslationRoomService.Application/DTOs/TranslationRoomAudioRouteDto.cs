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
    string? SourceDubVoiceId = null,

    /// <summary>
    /// WT-B — the voice this route's SPEAKER was cloned into in an EARLIER meeting, so this
    /// meeting can open in their own voice instead of spending its first twenty seconds in a
    /// stock one. Null when they have none.
    ///
    /// SEPARATE FROM <see cref="SourceDubVoiceId"/> ON PURPOSE, AND THAT SEPARATION IS THE
    /// FEATURE. A dub voice is a DELIBERATE PICK: the worker stops capturing and never
    /// overwrites it. This is the opposite — a starting point the worker is supposed to keep
    /// improving on. Delivered on one field, a carried clone would be read as a pick, capture
    /// would stop, and every speaker would be frozen at the first clone they ever earned.
    /// </summary>
    string? SourceAutoCloneVoiceId = null,

    /// <summary>
    /// How good the clip behind <see cref="SourceAutoCloneVoiceId"/> was (0..1) — the bar a
    /// later clip must beat before it replaces that voice.
    ///
    /// A STRING, AND NULL MEANS "NOT MEASURED" RATHER THAN ZERO. Zero grades as the worst
    /// possible sample and would invite replacement by literally any clip that clears the
    /// floors, so the absent case must stay distinguishable. Carried as text because it is
    /// formatted once, with InvariantCulture, and passed through unparsed — a decimal
    /// round-tripped through a comma-decimal locale is how 0.006575 became 6575 in billing.
    /// </summary>
    string? SourceAutoCloneScore = null
);

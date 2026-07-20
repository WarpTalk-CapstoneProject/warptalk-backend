using System;

namespace WarpTalk.TranslationRoomService.Domain.Entities;

/// <summary>
/// One TranslationRoom can be split into multiple time-bounded sessions (e.g. pause/resume
/// across reconnects, or multiple meeting occurrences under one room). NOT YET wired into the
/// STT ingestion pipeline: stt_worker / TranscriptRedisConsumerService.ProcessSttMessageAsync
/// still key everything by translation_room_id only (Redis stream keys stt:results:{roomId}).
/// This entity/table/API exists so the domain model is honestly describable in documentation
/// and so transcript.transcripts has a column to point at once that wiring pass happens — it is
/// deliberately NOT session-aware yet. See transcript.transcripts.translation_room_session_id
/// (migration 021) for the other half of this scope cut.
/// </summary>
public partial class TranslationRoomSession
{
    public Guid Id { get; set; }

    public Guid TranslationRoomId { get; set; }

    public string MainLanguage { get; set; } = null!;

    public string? AudioUrl { get; set; }

    public string Status { get; set; } = null!;

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranslationRoom TranslationRoom { get; set; } = null!;
}

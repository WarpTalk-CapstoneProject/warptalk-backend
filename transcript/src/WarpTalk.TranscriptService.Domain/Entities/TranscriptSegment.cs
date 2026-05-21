using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptSegment
{
    public Guid Id { get; set; }

    public Guid TranscriptId { get; set; }

    /// <summary>
    /// External TranslationRoomService participant id. No physical FK.
    /// </summary>
    public Guid? SpeakerParticipantId { get; set; }

    public string SpeakerName { get; set; } = null!;

    public string OriginalText { get; set; } = null!;

    public string OriginalLanguage { get; set; } = null!;

    public int StartTimeMs { get; set; }

    public int EndTimeMs { get; set; }

    public decimal? Confidence { get; set; }

    public int SequenceOrder { get; set; }

    public bool IsCorrected { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Transcript Transcript { get; set; } = null!;

    public virtual ICollection<TranscriptCorrection> TranscriptCorrections { get; set; } = new List<TranscriptCorrection>();

    public virtual ICollection<TranscriptTranslation> TranscriptTranslations { get; set; } = new List<TranscriptTranslation>();
}

using System;
using System.Collections.Generic;

namespace WarpTalk.TranscriptService.Domain.Entities;

public partial class TranscriptTranslation
{
    public Guid Id { get; set; }

    public Guid SegmentId { get; set; }

    public string TargetLanguage { get; set; } = null!;

    public string TranslatedText { get; set; } = null!;

    public string TranslatorModel { get; set; } = null!;

    public decimal? Confidence { get; set; }

    public bool IsRetranslated { get; set; }

    public int? LatencyMs { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual TranscriptSegment Segment { get; set; } = null!;
}

using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// A system-managed term applied to every workspace (unless a workspace opts out via
/// AiUsagePolicy.UseGlobalGlossary), merged into the same STT/MT prompts as workspace-level
/// glossary terms — workspace terms always take precedence on a key collision. See
/// docs/global-glossary-plan.md.
/// </summary>
public partial class GlobalGlossaryTerm
{
    public Guid Id { get; set; }

    public string Term { get; set; } = null!;

    public string PreferredTranslation { get; set; } = null!;

    public string? SourceLanguage { get; set; }

    public string? TargetLanguage { get; set; }

    public string? BusinessDomain { get; set; }

    public string? Definition { get; set; }

    public string? UsageNote { get; set; }

    public int Priority { get; set; }

    /// <summary>draft | published | archived — only "published" rows are read by
    /// GlossaryStartedEventConsumer.</summary>
    public string Status { get; set; } = null!;

    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
}

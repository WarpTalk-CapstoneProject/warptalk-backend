using System;

namespace WarpTalk.TranscriptService.Domain.Entities;

/// <summary>
/// Audit trail for transcript.global_glossary_terms mutations — required (not optional) because
/// a bad global term can degrade STT/MT for every workspace at once; see
/// docs/global-glossary-plan.md §6.
/// </summary>
public partial class GlobalGlossaryAudit
{
    public Guid Id { get; set; }

    public Guid TermId { get; set; }

    /// <summary>created | updated | published | archived | deleted</summary>
    public string Action { get; set; } = null!;

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public Guid ActorUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}

using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceKnowledgeGlossary
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = null!;

    public string? BusinessDomain { get; set; }

    public string SourceLanguage { get; set; } = null!;

    public string TargetLanguage { get; set; } = null!;

    public string Term { get; set; } = null!;

    public string PreferredTranslation { get; set; } = null!;

    public string? PartOfSpeech { get; set; }

    public string? Definition { get; set; }

    public string? UsageNote { get; set; }

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    public virtual Workspace Workspace { get; set; } = null!;
}

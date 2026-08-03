using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class WorkspaceDocument
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid? UploadedBy { get; set; }

    public Guid? OwnerId { get; set; }

    public string Name { get; set; } = null!;

    public string FileName { get; set; } = null!;

    public string FileExtension { get; set; } = null!;

    public string MimeType { get; set; } = null!;

    public long SizeBytes { get; set; }

    public string StorageProvider { get; set; } = null!;

    public string StorageKey { get; set; } = null!;

    public string SourceType { get; set; } = null!;

    public Guid? SourceId { get; set; }

    public string DocumentType { get; set; } = null!;

    public string? SourceLanguage { get; set; }

    public string? DetectedLanguage { get; set; }

    public string? BusinessDomain { get; set; }

    public string? Summary { get; set; }

    public string? Keywords { get; set; }

    public bool AiEligible { get; set; }

    public string? AiUsagePolicy { get; set; }

    public string IngestionStatus { get; set; } = null!;

    public DateTime? LastIndexedAt { get; set; }

    public string? IndexVersion { get; set; }

    public string ConfidentialityLevel { get; set; } = null!;

    public string RetentionState { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    public bool IsAiAllowed { get; set; }

    public virtual Workspace Workspace { get; set; } = null!;

    public virtual ICollection<WorkspaceDocumentAccessPolicy> WorkspaceDocumentAccessPolicies { get; set; } = new List<WorkspaceDocumentAccessPolicy>();

    public virtual ICollection<WorkspaceDocumentAudit> WorkspaceDocumentAudits { get; set; } = new List<WorkspaceDocumentAudit>();
}

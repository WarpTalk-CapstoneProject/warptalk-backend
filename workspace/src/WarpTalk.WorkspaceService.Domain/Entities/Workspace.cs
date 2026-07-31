using System;
using System.Collections.Generic;

namespace WarpTalk.WorkspaceService.Domain.Entities;

public partial class Workspace
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Slug { get; set; } = null!;

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid OwnerId { get; set; }

    public string? LogoUrl { get; set; }

    public bool AllowExternalCollaboration { get; set; }

    public bool RequireVerifiedDomainForInternal { get; set; }

    public bool AllowSubdomains { get; set; }

    public string Settings { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Internal auth user reference.
    /// </summary>
    public Guid? DeletedBy { get; set; }

    public virtual ICollection<WorkspaceDocumentAccessPolicy> WorkspaceDocumentAccessPolicies { get; set; } = new List<WorkspaceDocumentAccessPolicy>();

    public virtual ICollection<WorkspaceDocumentAudit> WorkspaceDocumentAudits { get; set; } = new List<WorkspaceDocumentAudit>();

    public virtual ICollection<WorkspaceDocument> WorkspaceDocuments { get; set; } = new List<WorkspaceDocument>();

    public virtual ICollection<WorkspaceInvitation> WorkspaceInvitations { get; set; } = new List<WorkspaceInvitation>();

    public virtual ICollection<WorkspaceMember> WorkspaceMembers { get; set; } = new List<WorkspaceMember>();

    public virtual ICollection<WorkspaceVerifiedDomain> WorkspaceVerifiedDomains { get; set; } = new List<WorkspaceVerifiedDomain>();
}

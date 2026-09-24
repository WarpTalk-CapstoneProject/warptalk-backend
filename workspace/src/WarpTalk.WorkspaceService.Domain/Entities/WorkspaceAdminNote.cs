using System;

namespace WarpTalk.WorkspaceService.Domain.Entities;

/// <summary>
/// An internal note a platform administrator left on a workspace: "called the owner, invoice
/// promised Friday". Platform-side only — never shown to the tenant — and append-only, like the
/// audit log beside it: a correction is a new note, so what was known when stays readable.
/// </summary>
public partial class WorkspaceAdminNote
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public string Body { get; set; } = null!;

    public Guid AuthorId { get; set; }

    public DateTime CreatedAt { get; set; }
}

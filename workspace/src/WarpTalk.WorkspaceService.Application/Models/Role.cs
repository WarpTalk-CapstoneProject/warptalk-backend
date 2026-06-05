using System;

namespace WarpTalk.WorkspaceService.Application.Models;

/// <summary>
/// Read model for Auth role data (not persisted in workspace DB). Resolved via Auth gRPC.
/// </summary>
public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
}

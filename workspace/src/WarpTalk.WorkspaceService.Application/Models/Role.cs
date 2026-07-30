using System;

namespace WarpTalk.WorkspaceService.Application.Models;

/// <summary>
/// Read model for Auth role data (not persisted in workspace DB). Resolved via Auth gRPC.
/// </summary>
public record Role
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
}

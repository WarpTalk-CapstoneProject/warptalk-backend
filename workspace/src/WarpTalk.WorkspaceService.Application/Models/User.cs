using System;

namespace WarpTalk.WorkspaceService.Application.Models;

/// <summary>
/// Read model for Auth user data (not persisted in workspace DB). Resolved via Auth gRPC.
/// </summary>
public record User
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = null!;
    public string Email { get; init; } = null!;
    public string? AvatarUrl { get; init; }
    public string PreferredLanguage { get; init; } = "en";
}

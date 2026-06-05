using System;

namespace WarpTalk.WorkspaceService.Application.Models;

/// <summary>
/// Read model for Auth user data (not persisted in workspace DB). Resolved via Auth gRPC.
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public string PreferredLanguage { get; set; } = "en";
}

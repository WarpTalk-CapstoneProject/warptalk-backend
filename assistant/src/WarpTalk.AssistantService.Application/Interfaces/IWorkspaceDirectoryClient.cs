namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>What a notification about a workspace needs to say about it.</summary>
/// <param name="OwnerUserId">Null when the workspace service did not report one.</param>
public record WorkspaceProfile(Guid WorkspaceId, string Name, string Slug, Guid? OwnerUserId);

public interface IWorkspaceDirectoryClient
{
    /// <summary>
    /// The workspace's name, slug and Owner. Null when the workspace service cannot be reached or
    /// does not know the workspace; never throws.
    /// </summary>
    Task<WorkspaceProfile?> GetProfileAsync(Guid workspaceId, CancellationToken ct = default);
}

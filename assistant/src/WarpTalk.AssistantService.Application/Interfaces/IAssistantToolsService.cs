using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Interfaces;

/// <summary>
/// Reads the AI worker's built-in tool manifest as it is stored. Null when there is none, or it
/// could not be read; never throws for a Redis failure.
/// </summary>
public interface IAssistantToolManifestSource
{
    Task<string?> GetManifestJsonAsync(CancellationToken ct = default);
}

/// <summary>What WarpBot can reach for, for one member of one workspace. Backs the tools page.</summary>
public interface IAssistantToolsService
{
    /// <param name="callerIsPlatformStaff">
    /// Decided by the caller with the platform-staff check, so <c>platform_staff</c> tools are listed
    /// only to staff.
    /// </param>
    /// <returns>
    /// A failure with <c>PluginConstants.ErrorCodes.PermissionDenied</c> when the caller is not an
    /// active member of the workspace.
    /// </returns>
    Task<Result<AssistantToolsDto>> GetToolsAsync(
        Guid userId,
        Guid workspaceId,
        bool callerIsPlatformStaff,
        CancellationToken ct = default);
}

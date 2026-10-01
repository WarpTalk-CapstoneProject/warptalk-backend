using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.Shared;
using WarpTalk.Shared.PlatformSettings;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// The tools page's source of truth: the worker's own built-in registry (from its Redis manifest),
/// the web search switch as the worker would resolve it, and the plugin tools exactly as
/// <see cref="IMcpToolOrchestrator"/> offers them.
/// </summary>
public sealed class AssistantToolsService : IAssistantToolsService
{
    private readonly IAssistantToolManifestSource _manifestSource;
    private readonly IWorkspacePluginGuard _workspacePluginGuard;
    private readonly IMcpToolOrchestrator _orchestrator;
    private readonly IPlatformSettings _platformSettings;

    public AssistantToolsService(
        IAssistantToolManifestSource manifestSource,
        IWorkspacePluginGuard workspacePluginGuard,
        IMcpToolOrchestrator orchestrator,
        IPlatformSettings platformSettings)
    {
        _manifestSource = manifestSource;
        _workspacePluginGuard = workspacePluginGuard;
        _orchestrator = orchestrator;
        _platformSettings = platformSettings;
    }

    public async Task<Result<AssistantToolsDto>> GetToolsAsync(
        Guid userId,
        Guid workspaceId,
        bool callerIsPlatformStaff,
        CancellationToken ct = default)
    {
        // The membership check the plugin catalog uses, and the availability the plugin list needs:
        // one call answers both. A non-member learns nothing, not even the built-in list.
        var availability = await _workspacePluginGuard.GetAvailabilityForMemberAsync(workspaceId, userId, ct);
        if (!availability.IsSuccess)
            return Result.Failure<AssistantToolsDto>(availability.Error!, availability.ErrorCode!);

        var manifest = AssistantToolManifest.TryParse(await _manifestSource.GetManifestJsonAsync(ct));

        var builtIn = manifest?.Tools
            .Where(tool => callerIsPlatformStaff
                || !string.Equals(tool.Audience, AssistantToolConstants.Audience.PlatformStaff, StringComparison.Ordinal))
            .ToList()
            ?? [];

        var webSearch = new AssistantWebSearchDto(await ResolveWebSearchStateAsync(manifest, workspaceId, ct));

        var offered = await _orchestrator.ListOfferedPluginToolsAsync(userId, availability.Value!, ct: ct);
        var plugins = offered
            .Select(group => new AssistantPluginToolGroupDto(
                group.PluginKey,
                group.Label,
                group.Tools
                    .Select(tool => new AssistantPluginToolDto(
                        tool.Name,
                        tool.Label,
                        tool.Description,
                        tool.Effect,
                        tool.Policy ?? PluginConstants.ToolPolicy.DefaultFor(tool.Effect),
                        tool.WorkspacePolicy))
                    .ToList()))
            .ToList();

        return Result.Success(new AssistantToolsDto(
            ManifestAvailable: manifest is not null,
            ManifestGeneratedAt: manifest?.GeneratedAt,
            BuiltIn: builtIn,
            WebSearch: webSearch,
            Plugins: plugins));
    }

    /// <summary>
    /// The worker's own rule (ai_assistant_worker <c>_web_search_enabled</c>): its environment
    /// ceiling, narrowed by <c>flags.warpbot_web_search</c> read for the workspace. That flag's
    /// deny and allow lists are the only per-workspace switch there is.
    /// </summary>
    private async Task<string> ResolveWebSearchStateAsync(
        AssistantToolManifest? manifest,
        Guid workspaceId,
        CancellationToken ct)
    {
        if (manifest is null) return AssistantToolConstants.WebSearchState.Unknown;
        if (!manifest.WebSearchAvailable) return AssistantToolConstants.WebSearchState.Unavailable;

        var enabled = await _platformSettings.IsEnabledAsync(
            PlatformSettingsCatalog.FlagWarpBotWebSearch,
            new SettingContext(workspaceId),
            ct: ct);

        return enabled
            ? AssistantToolConstants.WebSearchState.On
            : AssistantToolConstants.WebSearchState.Off;
    }
}

using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Application.Mappers;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Reads and writes <c>workspace_plugin_tool_policies</c>. Enforcement is not here: it is in
/// <see cref="McpToolOrchestrator"/>, on the tool list WarpBot is offered and on every execution.
/// </summary>
/// <remarks>
/// Authorisation comes from the workspace service per call, against the workspace in the path, and
/// fails closed: an unreachable service reads as "not a member". Owner or Admin reads; only the
/// Owner writes, like every other change to the workspace's plugins.
/// </remarks>
public class WorkspaceToolPolicyService : IWorkspaceToolPolicyService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMembershipClient _membershipClient;

    public WorkspaceToolPolicyService(IUnitOfWork unitOfWork, IWorkspaceMembershipClient membershipClient)
    {
        _unitOfWork = unitOfWork;
        _membershipClient = membershipClient;
    }

    public async Task<Result<WorkspaceToolPoliciesDto>> GetAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        CancellationToken ct = default)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerId, ct);
        if (!membership.IsOwnerOrAdmin)
            return Denied(WorkspacePluginConstants.Messages.OwnerOrAdminOnly);

        var plugin = await FindPluginAsync(workspaceId, pluginKey, ct);
        if (plugin is null) return UnknownPlugin();

        return Result.Success(await ReadAsync(workspaceId, plugin, membership.IsOwner, ct));
    }

    public async Task<Result<WorkspaceToolPoliciesDto>> SetAsync(
        Guid workspaceId,
        Guid callerId,
        string pluginKey,
        UpdateWorkspaceToolPolicyRequest request,
        CancellationToken ct = default)
    {
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerId, ct);
        if (!membership.IsOwner)
            return Denied("Only the workspace Owner can change what WarpBot may do with a plugin's tools.");

        var plugin = await FindPluginAsync(workspaceId, pluginKey, ct);
        if (plugin is null) return UnknownPlugin();

        var toolName = request.ToolName?.Trim() ?? string.Empty;
        var definition = PluginDefinitionMapper.ToDefinition(plugin);
        if (!definition.Tools.Any(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)))
            return Result.Failure<WorkspaceToolPoliciesDto>(
                $"{definition.Label} has no tool named '{toolName}'.",
                PluginConstants.ErrorCodes.InvalidToolPolicy);

        var policy = string.IsNullOrWhiteSpace(request.Policy) ? null : request.Policy.Trim();
        if (policy is not null && !PluginConstants.ToolPolicy.IsWorkspaceRule(policy))
            return Result.Failure<WorkspaceToolPoliciesDto>(
                "A workspace rule is 'approval' or 'blocked'; send null to leave the tool to each member.",
                PluginConstants.ErrorCodes.InvalidToolPolicy);

        var existing = await _unitOfWork.WorkspacePluginToolPolicyRepository.FirstOrDefaultAsync(
            r => r.WorkspaceId == workspaceId && r.PluginId == plugin.Id && r.ToolName == toolName,
            ct: ct);

        if (policy is null)
        {
            if (existing is not null)
            {
                _unitOfWork.WorkspacePluginToolPolicyRepository.Remove(existing);
                await _unitOfWork.SaveChangesAsync(ct);
            }
        }
        else if (existing is null)
        {
            await _unitOfWork.WorkspacePluginToolPolicyRepository.AddAsync(new WorkspacePluginToolPolicy
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                PluginId = plugin.Id,
                ToolName = toolName,
                Policy = policy,
                SetBy = callerId,
                SetAt = DateTime.UtcNow,
            }, ct);
            await _unitOfWork.SaveChangesAsync(ct);
        }
        else if (existing.Policy != policy)
        {
            existing.Policy = policy;
            existing.SetBy = callerId;
            existing.SetAt = DateTime.UtcNow;
            _unitOfWork.WorkspacePluginToolPolicyRepository.Update(existing);
            await _unitOfWork.SaveChangesAsync(ct);
        }

        return Result.Success(await ReadAsync(workspaceId, plugin, canManage: true, ct));
    }

    /// <summary>
    /// An active plugin this workspace can see: any marketplace row, or a private row it owns. A
    /// private plugin of another workspace is unknown here, not forbidden, so its key reveals nothing.
    /// </summary>
    private async Task<Plugin?> FindPluginAsync(Guid workspaceId, string pluginKey, CancellationToken ct)
    {
        var key = pluginKey?.Trim() ?? string.Empty;
        var plugin = await _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            p => p.PluginKey == key && p.IsActive, ct: ct);
        if (plugin is null) return null;
        return plugin.OwnerWorkspaceId is null || plugin.OwnerWorkspaceId == workspaceId ? plugin : null;
    }

    private async Task<WorkspaceToolPoliciesDto> ReadAsync(
        Guid workspaceId,
        Plugin plugin,
        bool canManage,
        CancellationToken ct)
    {
        var definition = PluginDefinitionMapper.ToDefinition(plugin);
        var rules = WorkspaceToolRules.ForPlugin(
            await WorkspaceToolRules.LoadAsync(_unitOfWork, workspaceId, [plugin.Id], ct),
            plugin.Id);

        return new WorkspaceToolPoliciesDto(
            definition.Key,
            definition.Label,
            canManage,
            definition.Tools
                .Select(tool => new WorkspaceToolPolicyItemDto(
                    tool.Name,
                    tool.Label,
                    tool.Description,
                    tool.Effect,
                    WorkspaceToolRules.For(rules, tool.Name)))
                .ToList());
    }

    private static Result<WorkspaceToolPoliciesDto> Denied(string message) =>
        Result.Failure<WorkspaceToolPoliciesDto>(message, PluginConstants.ErrorCodes.PermissionDenied);

    private static Result<WorkspaceToolPoliciesDto> UnknownPlugin() =>
        Result.Failure<WorkspaceToolPoliciesDto>("Unknown plugin.", PluginConstants.ErrorCodes.UnknownPlugin);
}

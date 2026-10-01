namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>One tool of a plugin, with the workspace Owner's rule for it.</summary>
/// <param name="WorkspacePolicy"><c>approval</c>, <c>blocked</c>, or null for "member's choice".</param>
public record WorkspaceToolPolicyItemDto(
    string Name,
    string Label,
    string Description,
    string Effect,
    string? WorkspacePolicy);

/// <summary>A plugin's tools and the rules this workspace has for them.</summary>
/// <param name="CanManage">True for the Owner only; an Admin reads the same list without actions.</param>
public record WorkspaceToolPoliciesDto(
    string PluginKey,
    string PluginLabel,
    bool CanManage,
    IReadOnlyList<WorkspaceToolPolicyItemDto> Tools);

/// <summary>Sets or clears one tool's rule. A null <paramref name="Policy"/> is "member's choice".</summary>
public record UpdateWorkspaceToolPolicyRequest(string ToolName, string? Policy);

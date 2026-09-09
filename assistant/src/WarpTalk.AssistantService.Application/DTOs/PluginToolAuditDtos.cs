namespace WarpTalk.AssistantService.Application.DTOs;

/// <summary>
/// One recorded plugin tool call, as a workspace Owner or Admin sees it. WT-646.
/// </summary>
/// <remarks>
/// Deliberately narrower than the <c>plugin_tool_audits</c> row. <c>input_summary</c> is the first
/// 500 characters of the tool's arguments - a member's own search terms, calendar titles, file
/// names - and it is not exposed here. What an Owner needs in order to govern plugin use is which
/// plugin, which tool, by whom, when, and whether it worked; the argument text would turn a usage
/// log into a record of what each member typed, which is a different and much larger decision than
/// this ticket makes.
/// </remarks>
public record PluginToolAuditDto(
    Guid Id,
    Guid UserId,
    Guid? ConversationId,
    string PluginKey,
    string ToolName,
    /// <summary>
    /// "success", or the error code the call failed with - including the
    /// <c>workspace_plugin_not_allowed</c> refusals this policy itself produces.
    /// </summary>
    string ResultStatus,
    /// <summary>
    /// What the provider says the call touched (a file id, an event id), when it says anything.
    /// </summary>
    string? ProviderResourceRef,
    DateTime CreatedAt);

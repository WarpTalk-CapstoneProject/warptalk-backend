namespace WarpTalk.AssistantService.Application.DTOs;

// Wave 4: Insights over assistant_tool_calls. Serialised camelCase by the API, so the property names
// below are the JSON contract the web builds against - rename nothing.

/// <summary>Outcome counts over a window, and the median duration of the calls that reported one.</summary>
public record ToolInsightsTotalsDto(
    int Calls,
    int Ok,
    int Error,
    int Blocked,
    int NeedsSetup,
    int Declined,
    int ConfirmationRequired,
    int? MedianDurationMs);

/// <summary>Calls from one source: <c>builtin</c>, <c>plugin</c> or <c>web_search</c>.</summary>
public record ToolInsightsSourceDto(string Source, int Calls);

/// <summary>
/// One UTC day. <c>Failed</c> is error + blocked + needs_setup; declined and confirmation-required
/// calls are neither ok nor failed.
/// </summary>
public record ToolInsightsDayDto(string Date, int Builtin, int Plugin, int WebSearch, int Ok, int Failed);

/// <summary>One tool in one workspace's window.</summary>
public record ToolInsightsToolDto(
    string Tool,
    string Source,
    string? PluginKey,
    int Calls,
    int Ok,
    int Error,
    int Blocked,
    int NeedsSetup,
    int? MedianDurationMs,
    DateTime LastCalledAt);

/// <summary><c>GET /api/v1/assistant/workspaces/{workspaceId}/insights/tools</c>.</summary>
public record WorkspaceToolInsightsDto(
    DateTime From,
    DateTime To,
    DateTime? RecordingSince,
    ToolInsightsTotalsDto Totals,
    IReadOnlyList<ToolInsightsSourceDto> BySource,
    IReadOnlyList<ToolInsightsDayDto> ByDay,
    IReadOnlyList<ToolInsightsToolDto> ByTool,
    int PreviousPeriodCalls);

/// <summary>One tool across the platform: the workspace fields plus how many workspaces used it.</summary>
public record AdminToolInsightsToolDto(
    string Tool,
    string Source,
    string? PluginKey,
    int Calls,
    int Ok,
    int Error,
    int Blocked,
    int NeedsSetup,
    int? MedianDurationMs,
    DateTime LastCalledAt,
    int Workspaces);

/// <summary>One of the busiest workspaces. <c>Failed</c> is error + blocked + needs_setup.</summary>
public record ToolInsightsWorkspaceDto(Guid WorkspaceId, int Calls, int Failed);

/// <summary><c>GET /api/v1/assistant/admin/insights/tools</c>.</summary>
/// <param name="PreviousSuccessRate">
/// ok / calls in the previous window of the same length, as a fraction from 0 to 1; null when that
/// window had no calls.
/// </param>
public record AdminToolInsightsDto(
    DateTime From,
    DateTime To,
    DateTime? RecordingSince,
    ToolInsightsTotalsDto Totals,
    IReadOnlyList<ToolInsightsSourceDto> BySource,
    IReadOnlyList<ToolInsightsDayDto> ByDay,
    IReadOnlyList<AdminToolInsightsToolDto> ByTool,
    int PreviousPeriodCalls,
    IReadOnlyList<ToolInsightsWorkspaceDto> ByWorkspace,
    double? PreviousSuccessRate);

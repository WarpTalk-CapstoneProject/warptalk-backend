using WarpTalk.AssistantService.Domain.Entities;

namespace WarpTalk.AssistantService.Domain.Interfaces;

/// <summary>
/// One recorded tool call as the insights read it: metadata only, never arguments or results.
/// </summary>
public record AssistantToolCallInsightRow(
    Guid? WorkspaceId,
    string ToolName,
    string Source,
    string? PluginKey,
    string Outcome,
    int? DurationMs,
    DateTime CreatedAt);

public interface IAssistantToolCallRepository : IGenericRepository<AssistantToolCall>
{
    /// <summary>
    /// Every call in <c>[fromUtc, toUtc)</c>, in one workspace or (null) across all of them,
    /// projected to the columns the insights aggregate.
    /// </summary>
    Task<IReadOnlyList<AssistantToolCallInsightRow>> ListForInsightsAsync(
        Guid? workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Calls and successful calls in <c>[fromUtc, toUtc)</c>, in one workspace or (null) all.</summary>
    Task<(int Calls, int Ok)> CountForPeriodAsync(
        Guid? workspaceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>The earliest recorded call in the whole table, or null when it is empty.</summary>
    Task<DateTime?> GetEarliestCreatedAtAsync(CancellationToken ct = default);
}

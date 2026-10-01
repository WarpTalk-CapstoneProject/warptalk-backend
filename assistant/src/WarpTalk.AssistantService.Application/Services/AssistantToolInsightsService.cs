using System.Globalization;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.AssistantService.Application.Services;

/// <summary>
/// Aggregates <c>assistant_tool_calls</c> for the workspace Insights → Tools tab and the admin
/// WarpBot tools page (wave 4).
/// </summary>
/// <remarks>
/// The rows of the window are read as a narrow projection and aggregated here: medians per tool are
/// simplest in memory, and the window is bounded at <see cref="MaxWindow"/>. The median is
/// <c>percentile_cont(0.5)</c>'s: the middle value, or the mean of the two middle values.
/// </remarks>
public class AssistantToolInsightsService : IAssistantToolInsightsService
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    /// <summary>A longer window is clamped to its last 180 days.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(180);

    public const int TopWorkspaces = 20;

    private static readonly string[] SourceOrder =
    [
        AssistantToolCallConstants.Sources.Builtin,
        AssistantToolCallConstants.Sources.Plugin,
        AssistantToolCallConstants.Sources.WebSearch,
    ];

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceMembershipClient _membershipClient;
    private readonly TimeProvider _timeProvider;

    public AssistantToolInsightsService(
        IUnitOfWork unitOfWork,
        IWorkspaceMembershipClient membershipClient,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _membershipClient = membershipClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<WorkspaceToolInsightsDto>> GetWorkspaceAsync(
        Guid workspaceId, Guid callerUserId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        // Authorised before anything is read, exactly as the plugin audits are: the workspace
        // service's answer, failing closed when it cannot be reached.
        var membership = await _membershipClient.GetMembershipAsync(workspaceId, callerUserId, ct);
        if (!membership.IsOwnerOrAdmin)
            return Result.Failure<WorkspaceToolInsightsDto>(
                "Only a workspace Owner or Admin may read WarpBot tool usage for a workspace.",
                PluginConstants.ErrorCodes.PermissionDenied);

        if (!TryResolveWindow(from, to, out var window, out var error))
            return Result.Failure<WorkspaceToolInsightsDto>(error!, ErrorCodes.ValidationError);

        var rows = await _unitOfWork.AssistantToolCallRepository.ListForInsightsAsync(workspaceId, window.From, window.To, ct);
        var previous = await _unitOfWork.AssistantToolCallRepository.CountForPeriodAsync(
            workspaceId, window.PreviousFrom, window.From, ct);
        var recordingSince = await _unitOfWork.AssistantToolCallRepository.GetEarliestCreatedAtAsync(ct);

        return Result.Success(new WorkspaceToolInsightsDto(
            window.From,
            window.To,
            AsUtc(recordingSince),
            Totals(rows),
            BySource(rows),
            ByDay(rows, window),
            ByTool(rows).Select(tool => tool.ToWorkspaceDto()).ToList(),
            previous.Calls));
    }

    public async Task<Result<AdminToolInsightsDto>> GetPlatformAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        if (!TryResolveWindow(from, to, out var window, out var error))
            return Result.Failure<AdminToolInsightsDto>(error!, ErrorCodes.ValidationError);

        var rows = await _unitOfWork.AssistantToolCallRepository.ListForInsightsAsync(null, window.From, window.To, ct);
        var previous = await _unitOfWork.AssistantToolCallRepository.CountForPeriodAsync(
            null, window.PreviousFrom, window.From, ct);
        var recordingSince = await _unitOfWork.AssistantToolCallRepository.GetEarliestCreatedAtAsync(ct);

        var byWorkspace = rows
            .Where(row => row.WorkspaceId.HasValue)
            .GroupBy(row => row.WorkspaceId!.Value)
            .Select(group => new ToolInsightsWorkspaceDto(group.Key, group.Count(), group.Count(row => IsFailed(row.Outcome))))
            .OrderByDescending(workspace => workspace.Calls)
            .ThenBy(workspace => workspace.WorkspaceId)
            .Take(TopWorkspaces)
            .ToList();

        return Result.Success(new AdminToolInsightsDto(
            window.From,
            window.To,
            AsUtc(recordingSince),
            Totals(rows),
            BySource(rows),
            ByDay(rows, window),
            ByTool(rows).Select(tool => tool.ToAdminDto()).ToList(),
            previous.Calls,
            byWorkspace,
            previous.Calls == 0 ? null : (double)previous.Ok / previous.Calls));
    }

    /// <summary>The window the caller asked for, defaulted and clamped; all three bounds UTC.</summary>
    public readonly record struct InsightsWindow(DateTime From, DateTime To)
    {
        /// <summary>Start of the window of the same length immediately before this one.</summary>
        public DateTime PreviousFrom => From - (To - From);
    }

    public bool TryResolveWindow(DateTime? from, DateTime? to, out InsightsWindow window, out string? error)
    {
        var resolvedTo = to.HasValue ? AsUtc(to.Value) : _timeProvider.GetUtcNow().UtcDateTime;
        var resolvedFrom = from.HasValue ? AsUtc(from.Value) : resolvedTo - DefaultWindow;

        if (resolvedFrom >= resolvedTo)
        {
            window = default;
            error = "'from' must be earlier than 'to'.";
            return false;
        }

        if (resolvedTo - resolvedFrom > MaxWindow)
            resolvedFrom = resolvedTo - MaxWindow;

        window = new InsightsWindow(resolvedFrom, resolvedTo);
        error = null;
        return true;
    }

    internal static ToolInsightsTotalsDto Totals(IReadOnlyCollection<AssistantToolCallInsightRow> rows)
    {
        var counts = OutcomeCounts.Of(rows);
        return new ToolInsightsTotalsDto(
            rows.Count,
            counts.Ok,
            counts.Error,
            counts.Blocked,
            counts.NeedsSetup,
            counts.Declined,
            counts.ConfirmationRequired,
            Median(rows));
    }

    internal static IReadOnlyList<ToolInsightsSourceDto> BySource(IReadOnlyCollection<AssistantToolCallInsightRow> rows) =>
        SourceOrder
            .Select(source => new ToolInsightsSourceDto(source, rows.Count(row => row.Source == source)))
            .ToList();

    /// <summary>Every UTC day the window touches, in order, zero days included.</summary>
    internal static IReadOnlyList<ToolInsightsDayDto> ByDay(
        IReadOnlyCollection<AssistantToolCallInsightRow> rows, InsightsWindow window)
    {
        var byDate = rows.ToLookup(row => AsUtc(row.CreatedAt).Date);
        var first = window.From.Date;
        var last = window.To.AddTicks(-1).Date;

        var days = new List<ToolInsightsDayDto>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var dayRows = byDate[day].ToList();
            var counts = OutcomeCounts.Of(dayRows);
            days.Add(new ToolInsightsDayDto(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dayRows.Count(row => row.Source == AssistantToolCallConstants.Sources.Builtin),
                dayRows.Count(row => row.Source == AssistantToolCallConstants.Sources.Plugin),
                dayRows.Count(row => row.Source == AssistantToolCallConstants.Sources.WebSearch),
                counts.Ok,
                counts.Failed));
        }

        return days;
    }

    internal static IReadOnlyList<ToolAggregate> ByTool(IReadOnlyCollection<AssistantToolCallInsightRow> rows) =>
        rows
            .GroupBy(row => (row.ToolName, row.Source, row.PluginKey))
            .Select(group =>
            {
                var toolRows = group.ToList();
                var counts = OutcomeCounts.Of(toolRows);
                return new ToolAggregate(
                    group.Key.ToolName,
                    group.Key.Source,
                    group.Key.PluginKey,
                    toolRows.Count,
                    counts,
                    Median(toolRows),
                    AsUtc(toolRows.Max(row => row.CreatedAt)),
                    toolRows.Where(row => row.WorkspaceId.HasValue).Select(row => row.WorkspaceId!.Value).Distinct().Count());
            })
            .OrderByDescending(tool => tool.Calls)
            .ThenBy(tool => tool.Tool, StringComparer.Ordinal)
            .ThenBy(tool => tool.Source, StringComparer.Ordinal)
            .ToList();

    /// <summary><c>percentile_cont(0.5)</c> over the calls that reported a duration, rounded to a millisecond.</summary>
    internal static int? Median(IEnumerable<AssistantToolCallInsightRow> rows)
    {
        var durations = rows.Where(row => row.DurationMs.HasValue).Select(row => row.DurationMs!.Value).Order().ToList();
        if (durations.Count == 0)
            return null;
        var middle = durations.Count / 2;
        var median = durations.Count % 2 == 1
            ? durations[middle]
            : (durations[middle - 1] + (double)durations[middle]) / 2;
        return (int)Math.Round(median, MidpointRounding.AwayFromZero);
    }

    /// <summary>error + blocked + needs_setup. Anything that is not a known outcome counts as an error.</summary>
    internal static bool IsFailed(string outcome) =>
        outcome is not (AssistantToolCallConstants.Outcomes.Ok
            or AssistantToolCallConstants.Outcomes.Declined
            or AssistantToolCallConstants.Outcomes.ConfirmationRequired);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;

    internal readonly record struct OutcomeCounts(int Ok, int Error, int Blocked, int NeedsSetup, int Declined, int ConfirmationRequired)
    {
        public int Failed => Error + Blocked + NeedsSetup;

        public static OutcomeCounts Of(IEnumerable<AssistantToolCallInsightRow> rows)
        {
            int ok = 0, error = 0, blocked = 0, needsSetup = 0, declined = 0, confirmation = 0;
            foreach (var row in rows)
            {
                switch (row.Outcome)
                {
                    case AssistantToolCallConstants.Outcomes.Ok: ok++; break;
                    case AssistantToolCallConstants.Outcomes.Blocked: blocked++; break;
                    case AssistantToolCallConstants.Outcomes.NeedsSetup: needsSetup++; break;
                    case AssistantToolCallConstants.Outcomes.Declined: declined++; break;
                    case AssistantToolCallConstants.Outcomes.ConfirmationRequired: confirmation++; break;
                    default: error++; break;
                }
            }

            return new OutcomeCounts(ok, error, blocked, needsSetup, declined, confirmation);
        }
    }

    internal sealed record ToolAggregate(
        string Tool,
        string Source,
        string? PluginKey,
        int Calls,
        OutcomeCounts Counts,
        int? MedianDurationMs,
        DateTime LastCalledAt,
        int Workspaces)
    {
        public ToolInsightsToolDto ToWorkspaceDto() =>
            new(Tool, Source, PluginKey, Calls, Counts.Ok, Counts.Error, Counts.Blocked, Counts.NeedsSetup, MedianDurationMs, LastCalledAt);

        public AdminToolInsightsToolDto ToAdminDto() =>
            new(Tool, Source, PluginKey, Calls, Counts.Ok, Counts.Error, Counts.Blocked, Counts.NeedsSetup, MedianDurationMs, LastCalledAt, Workspaces);
    }
}

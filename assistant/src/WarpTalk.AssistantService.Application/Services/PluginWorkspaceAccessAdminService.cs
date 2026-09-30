using System.Text.Json;
using System.Text.RegularExpressions;
using WarpTalk.AssistantService.Application.DTOs;
using WarpTalk.AssistantService.Application.Helpers;
using WarpTalk.AssistantService.Application.Interfaces;
using WarpTalk.AssistantService.Domain.Constants;
using WarpTalk.AssistantService.Domain.Entities;
using WarpTalk.AssistantService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Events;

namespace WarpTalk.AssistantService.Application.Services;

/// <inheritdoc cref="IPluginWorkspaceAccessAdminService"/>
public partial class PluginWorkspaceAccessAdminService : IPluginWorkspaceAccessAdminService
{
    private const int MaxPlanSlugLength = 50;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkspaceDirectoryClient _directory;
    private readonly IAdminAuditRecorder _auditRecorder;

    public PluginWorkspaceAccessAdminService(
        IUnitOfWork unitOfWork,
        IWorkspaceDirectoryClient directory,
        IAdminAuditRecorder auditRecorder)
    {
        _unitOfWork = unitOfWork;
        _directory = directory;
        _auditRecorder = auditRecorder;
    }

    // ---- reads ----------------------------------------------------------------------------------

    public async Task<Result<PluginWorkspacesDto>> GetForPluginAsync(string pluginKey, CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginWorkspacesDto>(pluginKey);

        var cells = await PluginWorkspaceMatrix.BuildAsync(_unitOfWork, _directory, [plugin], null, ct);
        if (cells is null) return WorkspacesUnavailable<PluginWorkspacesDto>();

        return Result.Success(new PluginWorkspacesDto(
            plugin.PluginKey,
            plugin.Label,
            AvailabilityOf(plugin),
            cells.Select(ToRow).ToList()));
    }

    public async Task<Result<WorkspacePluginsAdminDto>> GetForWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var workspaces = await _directory.ListPlatformWorkspacesAsync(ct);
        if (workspaces is null) return WorkspacesUnavailable<WorkspacePluginsAdminDto>();
        var workspace = workspaces.FirstOrDefault(w => w.WorkspaceId == workspaceId);
        if (workspace is null) return UnknownWorkspace<WorkspacePluginsAdminDto>();

        // Retired rows too: an override on one is kept for the day it is reinstated, and the admin
        // should see that rather than have it disappear.
        var plugins = (await _unitOfWork.PluginRepository.FindAsync(p => p.OwnerWorkspaceId == null, ct: ct))
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cells = await PluginWorkspaceMatrix.BuildAsync(_unitOfWork, _directory, plugins, workspaceId, ct);
        if (cells is null) return WorkspacesUnavailable<WorkspacePluginsAdminDto>();

        return Result.Success(new WorkspacePluginsAdminDto(
            workspace.WorkspaceId,
            workspace.Name,
            workspace.PlanSlug,
            cells.Select(ToRow).ToList()));
    }

    // ---- the default ----------------------------------------------------------------------------

    public async Task<Result<PluginAvailabilityDto>> SetAvailabilityAsync(
        string pluginKey,
        SetPluginAvailabilityRequest request,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<PluginAvailabilityDto>(pluginKey);

        var chosen = request.Default?.Trim() ?? string.Empty;
        if (!PluginWorkspaceAccessConstants.Default.IsKnown(chosen))
            return Invalid<PluginAvailabilityDto>(
                $"'default' must be '{PluginWorkspaceAccessConstants.Default.Available}', "
                + $"'{PluginWorkspaceAccessConstants.Default.OptIn}' or '{PluginWorkspaceAccessConstants.Default.Retired}'.",
                PluginWorkspaceAccessConstants.ErrorCodes.InvalidAvailability);

        var plans = PluginWorkspaceAccess.Normalize(request.AllowedPlans ?? []);
        var badPlan = plans.FirstOrDefault(plan => plan.Length > MaxPlanSlugLength || !PlanSlugPattern().IsMatch(plan));
        if (badPlan is not null)
            return Invalid<PluginAvailabilityDto>(
                $"'{badPlan}' is not a plan slug.",
                PluginWorkspaceAccessConstants.ErrorCodes.InvalidAvailability);

        var before = PluginAuditSummary.Of(plugin);
        var wasActive = plugin.IsActive;

        if (chosen == PluginWorkspaceAccessConstants.Default.Retired)
        {
            // Retired is is_active = false, the same state a soft delete writes. The stored default
            // is left as it was, so reinstating the row brings back exactly the reach it had.
            plugin.IsActive = false;
        }
        else
        {
            plugin.IsActive = true;
            plugin.WorkspaceDefault = chosen;
        }

        // Empty means every plan, and is stored as null so there is one spelling of "no rule".
        plugin.AllowedPlanSlugsJson = plans.Count == 0 ? null : JsonSerializer.Serialize(plans);
        plugin.UpdatedBy = adminUserId == Guid.Empty ? null : adminUserId;
        plugin.UpdatedAt = DateTime.UtcNow;

        // Retiring and reinstating keep their own verbs: they are what an operator searches for.
        var action = wasActive == plugin.IsActive
            ? AdminAuditPluginActions.AvailabilitySet
            : PluginAuditSummary.UpdateAction(wasActive, plugin.IsActive);
        var recorded = await _auditRecorder.RecordPluginActionAsync(
            action, plugin.Id, adminUserId, before, PluginAuditSummary.Of(plugin), ct);
        if (!recorded.IsSuccess) return Result.Failure<PluginAvailabilityDto>(recorded.Error!, recorded.ErrorCode);

        _unitOfWork.PluginRepository.Update(plugin);
        await _unitOfWork.SaveChangesAsync(ct);
        return Result.Success(AvailabilityOf(plugin));
    }

    // ---- overrides ------------------------------------------------------------------------------

    public async Task<Result<ApplyPluginOverrideResultDto>> ApplyOverrideAsync(
        string pluginKey,
        ApplyPluginOverrideRequest request,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var plugin = await FindAsync(pluginKey, ct);
        if (plugin is null) return UnknownPlugin<ApplyPluginOverrideResultDto>(pluginKey);

        var action = request.Action?.Trim() ?? string.Empty;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var invalid = ValidateOverride(action, reason);
        if (invalid is not null) return Invalid<ApplyPluginOverrideResultDto>(invalid, PluginWorkspaceAccessConstants.ErrorCodes.InvalidOverride);

        var requestedIds = request.WorkspaceIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
        var plans = PluginWorkspaceAccess.Normalize(request.PlanSlugs ?? []);
        if (requestedIds.Count == 0 && plans.Count == 0)
            return Invalid<ApplyPluginOverrideResultDto>(
                "Choose the workspaces to change, a plan, or both.",
                PluginWorkspaceAccessConstants.ErrorCodes.InvalidOverride);
        if (requestedIds.Count > PluginWorkspaceAccessConstants.MaxBulkWorkspaces)
            return Invalid<ApplyPluginOverrideResultDto>(
                $"At most {PluginWorkspaceAccessConstants.MaxBulkWorkspaces} workspaces can be changed at once.",
                PluginWorkspaceAccessConstants.ErrorCodes.InvalidOverride);

        // The targets are resolved against the workspace service's own list, never taken on trust:
        // an id that names no workspace is refused rather than given an override row nobody reads.
        var workspaces = await _directory.ListPlatformWorkspacesAsync(ct);
        if (workspaces is null) return WorkspacesUnavailable<ApplyPluginOverrideResultDto>();
        var known = workspaces.ToDictionary(w => w.WorkspaceId);

        var unknown = requestedIds.Where(id => !known.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            return UnknownWorkspace<ApplyPluginOverrideResultDto>(
                $"{unknown.Count} of the chosen workspaces do not exist (or were deleted).");

        IEnumerable<PlatformWorkspace> targets = requestedIds.Count > 0
            ? requestedIds.Select(id => known[id])
            : workspaces;
        if (plans.Count > 0) targets = targets.Where(w => PluginWorkspaceAccess.MatchesPlan(plans, w.PlanSlug));
        var targetList = targets.ToList();
        if (targetList.Count > PluginWorkspaceAccessConstants.MaxBulkWorkspaces)
            return Invalid<ApplyPluginOverrideResultDto>(
                $"That plan covers {targetList.Count} workspaces; at most {PluginWorkspaceAccessConstants.MaxBulkWorkspaces} can be changed at once.",
                PluginWorkspaceAccessConstants.ErrorCodes.InvalidOverride);

        var applied = await ApplyAsync(plugin, targetList.Select(w => w.WorkspaceId).ToList(), action, reason, adminUserId, ct);
        if (!applied.IsSuccess) return Result.Failure<ApplyPluginOverrideResultDto>(applied.Error!, applied.ErrorCode);

        var changed = applied.Value!;
        return Result.Success(new ApplyPluginOverrideResultDto(
            plugin.PluginKey,
            action,
            changed.Count,
            targetList.Count - changed.Count,
            changed));
    }

    public async Task<Result<PluginWorkspaceRowDto>> SetWorkspaceOverrideAsync(
        Guid workspaceId,
        string pluginKey,
        SetWorkspacePluginOverrideRequest request,
        Guid adminUserId,
        CancellationToken ct = default)
    {
        var applied = await ApplyOverrideAsync(
            pluginKey,
            new ApplyPluginOverrideRequest(request.Action, request.Reason, [workspaceId]),
            adminUserId,
            ct);
        if (!applied.IsSuccess) return Result.Failure<PluginWorkspaceRowDto>(applied.Error!, applied.ErrorCode);

        var plugin = (await FindAsync(pluginKey, ct))!;
        var cells = await PluginWorkspaceMatrix.BuildAsync(_unitOfWork, _directory, [plugin], workspaceId, ct);
        var cell = cells?.FirstOrDefault();
        return cell is null
            ? WorkspacesUnavailable<PluginWorkspaceRowDto>()
            : Result.Success(ToRow(cell));
    }

    /// <summary>
    /// Writes (or removes) the override on each workspace, recording each change first.
    /// </summary>
    /// <returns>The workspaces that actually changed.</returns>
    private async Task<Result<IReadOnlyList<Guid>>> ApplyAsync(
        Plugin plugin,
        IReadOnlyList<Guid> workspaceIds,
        string action,
        string? reason,
        Guid adminUserId,
        CancellationToken ct)
    {
        if (workspaceIds.Count == 0) return Result.Success<IReadOnlyList<Guid>>([]);

        var ids = workspaceIds.ToList();
        var existing = (await _unitOfWork.WorkspacePluginOverrideRepository.FindAsync(
                row => row.PluginId == plugin.Id && ids.Contains(row.WorkspaceId),
                ct: ct))
            .GroupBy(row => row.WorkspaceId)
            .ToDictionary(group => group.Key, group => group.First());

        var now = DateTime.UtcNow;
        var setBy = adminUserId == Guid.Empty ? (Guid?)null : adminUserId;
        var changed = new List<Guid>();

        foreach (var workspaceId in ids)
        {
            existing.TryGetValue(workspaceId, out var current);
            var before = PluginAuditSummary.OfWorkspace(plugin, workspaceId, current);
            string auditAction;
            WorkspacePluginOverride? after;

            if (action == PluginWorkspaceAccessConstants.OverrideAction.Reset)
            {
                if (current is null) continue;
                _unitOfWork.WorkspacePluginOverrideRepository.Remove(current);
                auditAction = AdminAuditPluginActions.WorkspaceReset;
                after = null;
            }
            else
            {
                var state = action == PluginWorkspaceAccessConstants.OverrideAction.Enable
                    ? PluginWorkspaceAccessConstants.OverrideState.Enabled
                    : PluginWorkspaceAccessConstants.OverrideState.Disabled;
                if (current is not null && current.State == state && current.Reason == reason) continue;

                if (current is null)
                {
                    after = new WorkspacePluginOverride
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = workspaceId,
                        PluginId = plugin.Id,
                        State = state,
                        Reason = reason,
                        SetBy = setBy,
                        SetAt = now,
                    };
                    await _unitOfWork.WorkspacePluginOverrideRepository.AddAsync(after, ct);
                }
                else
                {
                    current.State = state;
                    current.Reason = reason;
                    current.SetBy = setBy;
                    current.SetAt = now;
                    _unitOfWork.WorkspacePluginOverrideRepository.Update(current);
                    after = current;
                }

                auditAction = state == PluginWorkspaceAccessConstants.OverrideState.Enabled
                    ? AdminAuditPluginActions.WorkspaceEnabled
                    : AdminAuditPluginActions.WorkspaceDisabled;
            }

            // Before the commit, and the whole apply abandoned if any record fails: a bulk change
            // that half-happened without its audit trail is the one outcome this must not have.
            var recorded = await _auditRecorder.RecordPluginWorkspaceActionAsync(
                auditAction,
                plugin.Id,
                workspaceId,
                adminUserId,
                reason,
                before,
                PluginAuditSummary.OfWorkspace(plugin, workspaceId, after),
                ct);
            if (!recorded.IsSuccess) return Result.Failure<IReadOnlyList<Guid>>(recorded.Error!, recorded.ErrorCode);

            changed.Add(workspaceId);
        }

        if (changed.Count == 0) return Result.Success<IReadOnlyList<Guid>>(changed);

        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (PersistenceConflict.IsUniqueViolation(ex))
        {
            // Two admins enabling the same plugin in the same workspace at once: the key kept the
            // data right, this keeps the answer right.
            return Result.Failure<IReadOnlyList<Guid>>(
                "Another change to these workspaces landed first. Refresh and try again.",
                WorkspacePluginConstants.ErrorCodes.ListChangedConcurrently);
        }

        return Result.Success<IReadOnlyList<Guid>>(changed);
    }

    private static string? ValidateOverride(string action, string? reason)
    {
        if (!PluginWorkspaceAccessConstants.OverrideAction.IsKnown(action))
            return $"'action' must be '{PluginWorkspaceAccessConstants.OverrideAction.Enable}', "
                + $"'{PluginWorkspaceAccessConstants.OverrideAction.Disable}' or '{PluginWorkspaceAccessConstants.OverrideAction.Reset}'.";

        // Turning a plugin off under a workspace that is using it is the change someone will ask
        // about later; the reason is what the answer is.
        if (action == PluginWorkspaceAccessConstants.OverrideAction.Disable && reason is null)
            return "Give a reason for turning the plugin off.";

        if (reason is { Length: > PluginWorkspaceAccessConstants.MaxReasonLength })
            return $"Keep the reason under {PluginWorkspaceAccessConstants.MaxReasonLength} characters.";

        return null;
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>A marketplace row, active or retired; never a workspace's private plugin.</summary>
    private Task<Plugin?> FindAsync(string pluginKey, CancellationToken ct)
    {
        var key = pluginKey?.Trim() ?? string.Empty;
        return _unitOfWork.PluginRepository.FirstOrDefaultAsync(
            plugin => plugin.PluginKey == key && plugin.OwnerWorkspaceId == null,
            ct: ct);
    }

    private static PluginAvailabilityDto AvailabilityOf(Plugin plugin) =>
        new(PluginWorkspaceAccess.DefaultOf(plugin), PluginWorkspaceAccess.AllowedPlans(plugin));

    private static PluginWorkspaceRowDto ToRow(PluginWorkspaceCell cell) =>
        new(
            cell.Workspace.WorkspaceId,
            cell.Workspace.Name,
            cell.Workspace.Slug,
            cell.Workspace.Status,
            cell.Workspace.PlanSlug,
            cell.Workspace.MemberCount,
            cell.Plugin.PluginKey,
            cell.Plugin.Label,
            cell.Plugin.AvatarUrl,
            cell.Plugin.Kind,
            PluginWorkspaceAccess.DefaultOf(cell.Plugin),
            PluginWorkspaceAccess.AllowedPlans(cell.Plugin),
            cell.Verdict.Allowed,
            cell.Verdict.Source,
            cell.Override?.State,
            cell.Override?.Reason,
            cell.Override?.SetBy,
            cell.Override?.SetAt,
            cell.OnWorkspaceList,
            cell.InUse,
            cell.ConnectedUserIds,
            cell.UsageCount,
            cell.LastUsedAt);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex PlanSlugPattern();

    private static Result<T> UnknownPlugin<T>(string pluginKey) =>
        Result.Failure<T>($"No marketplace plugin is keyed '{pluginKey}'.", PluginConstants.ErrorCodes.UnknownPlugin);

    private static Result<T> UnknownWorkspace<T>(string? message = null) =>
        Result.Failure<T>(message ?? "No such workspace.", PluginWorkspaceAccessConstants.ErrorCodes.UnknownWorkspace);

    private static Result<T> WorkspacesUnavailable<T>() =>
        Result.Failure<T>(
            PluginWorkspaceAccessConstants.Messages.WorkspacesUnavailable,
            PluginWorkspaceAccessConstants.ErrorCodes.WorkspacesUnavailable);

    private static Result<T> Invalid<T>(string message, string errorCode) => Result.Failure<T>(message, errorCode);
}

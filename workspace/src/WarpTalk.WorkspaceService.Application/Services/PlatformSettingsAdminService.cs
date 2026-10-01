using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.Shared.Extensions;
using WarpTalk.Shared.PlatformSettings;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

public interface IPlatformSettingsAdminService
{
    Task<Result<PlatformSettingsConsoleDto>> GetAsync(ClaimsPrincipal user, CancellationToken ct = default);

    Task<Result<IReadOnlyList<PlatformSettingChangeDto>>> GetHistoryAsync(string? key, int limit, CancellationToken ct = default);

    Task<Result<PlatformSettingDto>> SetAsync(ClaimsPrincipal user, string key, PlatformSettingWriteRequest request, string? correlationId, CancellationToken ct = default);

    Task<Result<PlatformSettingDto>> ResetAsync(ClaimsPrincipal user, string key, PlatformSettingResetRequest request, string? correlationId, CancellationToken ct = default);

    Task<Result<PlatformSettingDto>> RevertAsync(ClaimsPrincipal user, Guid changeId, PlatformSettingRevertRequest request, string? correlationId, CancellationToken ct = default);

    Task<Result<PlatformSettingsExportDto>> ExportAsync(ClaimsPrincipal user, string? correlationId, CancellationToken ct = default);

    Task<Result<PlatformSettingsImportResultDto>> ImportAsync(ClaimsPrincipal user, PlatformSettingsImportRequest request, string? correlationId, CancellationToken ct = default);

    /// <summary>Loads every stored value and publishes it. The periodic re-publish and every write call this.</summary>
    Task<bool> PublishAsync(CancellationToken ct = default);
}

/// <summary>
/// The platform settings console: the registry (code) joined with what operators stored (database),
/// every write validated against the registry, recorded in the setting's history AND the admin audit
/// log in the same save, then published to Redis for the owning services.
///
/// Permissions: the controller requires settings.read for reads and settings.manage for writes; a
/// Security &amp; auth setting additionally needs settings.security, checked here per key because an
/// import or a revert can touch any key.
/// </summary>
public sealed class PlatformSettingsAdminService : IPlatformSettingsAdminService
{
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 1000;
    public const int MaxHistory = 200;
    public const int MaxImportEntries = 500;
    public const string ExportFormat = "warptalk.platform-settings/v1";

    private static readonly Regex PlanSlug = new("^[a-z0-9][a-z0-9_-]{0,49}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly IPlatformSettingValueRepository _values;
    private readonly IPlatformSettingChangeRepository _changes;
    private readonly IAdminAuditLogRepository _auditLog;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IStaffAccessResolver _access;
    private readonly IPlatformSettingsPublisher _publisher;
    private readonly IPlatformSettings? _localReader;
    private readonly ILogger<PlatformSettingsAdminService> _logger;
    private readonly TimeProvider _time;

    public PlatformSettingsAdminService(
        IPlatformSettingValueRepository values,
        IPlatformSettingChangeRepository changes,
        IAdminAuditLogRepository auditLog,
        IUnitOfWork unitOfWork,
        IStaffAccessResolver access,
        IPlatformSettingsPublisher publisher,
        ILogger<PlatformSettingsAdminService> logger,
        IPlatformSettings? localReader = null,
        TimeProvider? time = null)
    {
        _values = values;
        _changes = changes;
        _auditLog = auditLog;
        _unitOfWork = unitOfWork;
        _access = access;
        _publisher = publisher;
        _logger = logger;
        _localReader = localReader;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Reads ───────────────────────────────────────────────────────────────────────────────

    public async Task<Result<PlatformSettingsConsoleDto>> GetAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var access = await _access.GetAsync(user, ct);
        var canManage = access.Has(AdminPermissions.SettingsManage);
        var canSecurity = canManage && access.Has(AdminPermissions.SettingsSecurity);

        var stored = await _values.GetAllAsync(ct);
        var latest = await _changes.GetLatestPerKeyAsync(ct);

        var settings = PlatformSettingsCatalog.All
            .Select(definition => ToDto(definition, stored, latest, canManage, canSecurity))
            .ToList();
        var categories = SettingCategories.Ordered
            .Select(category => new PlatformSettingCategoryDto(
                category,
                settings.Count(s => s.Category == category),
                settings.Count(s => s.Category == category && (s.IsSet || s.Overrides.Count > 0))))
            .ToList();

        var state = _publisher.State;
        return Result.Success(new PlatformSettingsConsoleDto(
            categories,
            settings,
            new PlatformSettingsPublishStatusDto(state.Version, state.PublishedAt, state.Healthy, state.Error),
            canManage,
            canSecurity));
    }

    public async Task<Result<IReadOnlyList<PlatformSettingChangeDto>>> GetHistoryAsync(string? key, int limit, CancellationToken ct = default)
    {
        if (key is not null && PlatformSettingsCatalog.Find(key) is null)
            return Result.Failure<IReadOnlyList<PlatformSettingChangeDto>>("Unknown setting.", ErrorCodes.NotFound);

        var rows = await _changes.GetRecentAsync(key, Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxHistory), ct);
        return Result.Success<IReadOnlyList<PlatformSettingChangeDto>>(rows.Select(ToChangeDto).ToList());
    }

    // ── Writes ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<PlatformSettingDto>> SetAsync(
        ClaimsPrincipal user, string key, PlatformSettingWriteRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request is null) return Result.Failure<PlatformSettingDto>("A value is required.", ErrorCodes.ValidationError);
        var check = await PrepareAsync(user, key, request.ScopeType, request.ScopeId, request.Reason, ct);
        if (!check.IsSuccess) return Result.Failure<PlatformSettingDto>(check.Error!, check.ErrorCode);
        var (definition, actor, scopeType, scopeId, reason) = check.Value!;

        if (SettingValueValidator.Validate(definition, request.Value) is { } error)
            return Result.Failure<PlatformSettingDto>(error, ErrorCodes.ValidationError);
        var value = SettingValueValidator.Normalize(definition, request.Value);

        var row = await _values.GetAsync(key, scopeType, scopeId, ct);
        if (request.ExpectedVersion is not { } expected)
            return Result.Failure<PlatformSettingDto>("expectedVersion is required: send the version you last read (0 when not set).", ErrorCodes.ValidationError);
        if ((row?.Version ?? 0) != expected)
            return Result.Failure<PlatformSettingDto>(StaleMessage(definition), ErrorCodes.Conflict);

        if (row is not null && SettingValueValidator.AreEqual(Parse(row.ValueJson), value))
            return await CurrentAsync(user, definition, ct);

        var outcome = await ApplyAsync(definition, actor, scopeType, scopeId, row, value, PlatformSettingChangeActions.Set,
            AdminAuditPlatformSettingActions.Changed, reason, correlationId, null, ct);
        return outcome.IsSuccess ? await CurrentAsync(user, definition, ct) : Result.Failure<PlatformSettingDto>(outcome.Error!, outcome.ErrorCode);
    }

    public async Task<Result<PlatformSettingDto>> ResetAsync(
        ClaimsPrincipal user, string key, PlatformSettingResetRequest request, string? correlationId, CancellationToken ct = default)
    {
        request ??= new PlatformSettingResetRequest(null, null, null, null);
        var check = await PrepareAsync(user, key, request.ScopeType, request.ScopeId, request.Reason, ct);
        if (!check.IsSuccess) return Result.Failure<PlatformSettingDto>(check.Error!, check.ErrorCode);
        var (definition, actor, scopeType, scopeId, reason) = check.Value!;

        var row = await _values.GetAsync(key, scopeType, scopeId, ct);
        if (row is null) return await CurrentAsync(user, definition, ct);
        if (request.ExpectedVersion is { } expected && row.Version != expected)
            return Result.Failure<PlatformSettingDto>(StaleMessage(definition), ErrorCodes.Conflict);

        var outcome = await ApplyAsync(definition, actor, scopeType, scopeId, row, null, PlatformSettingChangeActions.Reset,
            AdminAuditPlatformSettingActions.Reset, reason, correlationId, null, ct);
        return outcome.IsSuccess ? await CurrentAsync(user, definition, ct) : Result.Failure<PlatformSettingDto>(outcome.Error!, outcome.ErrorCode);
    }

    public async Task<Result<PlatformSettingDto>> RevertAsync(
        ClaimsPrincipal user, Guid changeId, PlatformSettingRevertRequest request, string? correlationId, CancellationToken ct = default)
    {
        var change = await _changes.GetAsync(changeId, ct);
        if (change is null) return Result.Failure<PlatformSettingDto>("That change does not exist.", ErrorCodes.NotFound);

        var check = await PrepareAsync(user, change.SettingKey, change.ScopeType, change.ScopeId, request?.Reason, ct);
        if (!check.IsSuccess) return Result.Failure<PlatformSettingDto>(check.Error!, check.ErrorCode);
        var (definition, actor, scopeType, scopeId, reason) = check.Value!;

        JsonElement? target = null;
        if (change.OldValueJson is not null)
        {
            var old = Parse(change.OldValueJson);
            // The registry may have tightened since: a value that no longer validates is not restored.
            if (SettingValueValidator.Validate(definition, old) is { } error)
                return Result.Failure<PlatformSettingDto>($"The earlier value is no longer allowed: {error}", ErrorCodes.ValidationError);
            target = SettingValueValidator.Normalize(definition, old);
        }

        var row = await _values.GetAsync(definition.Key, scopeType, scopeId, ct);
        var unchanged = target is null ? row is null : row is not null && SettingValueValidator.AreEqual(Parse(row.ValueJson), target.Value);
        if (unchanged) return await CurrentAsync(user, definition, ct);

        var outcome = await ApplyAsync(definition, actor, scopeType, scopeId, row, target, PlatformSettingChangeActions.Revert,
            AdminAuditPlatformSettingActions.Reverted, reason ?? $"Reverted change {change.Id:D}.", correlationId, change.Id, ct);
        return outcome.IsSuccess ? await CurrentAsync(user, definition, ct) : Result.Failure<PlatformSettingDto>(outcome.Error!, outcome.ErrorCode);
    }

    public async Task<Result<PlatformSettingsExportDto>> ExportAsync(ClaimsPrincipal user, string? correlationId, CancellationToken ct = default)
    {
        if (user.GetUserId() is not { } actor)
            return Result.Failure<PlatformSettingsExportDto>("Invalid or missing user identity.", ErrorCodes.Unauthorized);

        var stored = await _values.GetAllAsync(ct);
        var excluded = PlatformSettingsCatalog.All.Where(d => d.Sensitive).Select(d => d.Key).ToList();
        var entries = stored
            .Where(v => PlatformSettingsCatalog.Find(v.SettingKey) is { Sensitive: false })
            .OrderBy(v => v.SettingKey, StringComparer.Ordinal).ThenBy(v => v.ScopeType, StringComparer.Ordinal).ThenBy(v => v.ScopeId, StringComparer.Ordinal)
            .Select(v => new PlatformSettingsExportEntryDto(v.SettingKey, v.ScopeType, v.ScopeId, Parse(v.ValueJson)))
            .ToList();

        await _auditLog.AppendAsync(AuditEntry(AdminAuditPlatformSettingActions.Exported, "*", "Platform settings", null,
            $"Exported {entries.Count} stored values.", actor, user.GetEmail(), correlationId, null,
            new Dictionary<string, object?> { ["count"] = entries.Count }), ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return Result.Success(new PlatformSettingsExportDto(ExportFormat, Now, entries, excluded));
    }

    public async Task<Result<PlatformSettingsImportResultDto>> ImportAsync(
        ClaimsPrincipal user, PlatformSettingsImportRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (user.GetUserId() is not { } actor)
            return Result.Failure<PlatformSettingsImportResultDto>("Invalid or missing user identity.", ErrorCodes.Unauthorized);
        if (request?.Settings is not { } entries || entries.Count == 0)
            return Result.Failure<PlatformSettingsImportResultDto>("The file lists no settings.", ErrorCodes.ValidationError);
        if (entries.Count > MaxImportEntries)
            return Result.Failure<PlatformSettingsImportResultDto>($"At most {MaxImportEntries} settings per import.", ErrorCodes.ValidationError);

        var reason = request.Reason?.Trim();
        if (!request.DryRun && (reason is null || reason.Length < MinReasonLength))
            return Result.Failure<PlatformSettingsImportResultDto>($"Give a reason of at least {MinReasonLength} characters.", ErrorCodes.ValidationError);
        if (reason is { Length: > MaxReasonLength })
            return Result.Failure<PlatformSettingsImportResultDto>($"A reason must be at most {MaxReasonLength} characters.", ErrorCodes.ValidationError);

        var access = await _access.GetAsync(user, ct);
        var canSecurity = access.Has(AdminPermissions.SettingsSecurity);
        var stored = (await _values.GetAllAsync(ct))
            .ToDictionary(v => (v.SettingKey, v.ScopeType, v.ScopeId));
        var seen = new HashSet<(string, string, string)>();

        var lines = new List<PlatformSettingsImportLineDto>();
        var plan = new List<(SettingDefinition Definition, string ScopeType, string ScopeId, JsonElement Value)>();
        foreach (var entry in entries)
        {
            var key = entry?.Key ?? string.Empty;
            var definition = PlatformSettingsCatalog.Find(key);
            var (scopeType, scopeId, scopeError) = NormalizeScope(definition, entry?.ScopeType, entry?.ScopeId);
            string? error = definition is null ? "Unknown setting."
                : scopeError
                ?? (definition.RequiresSecurityPermission && !canSecurity ? "Needs the settings.security permission." : null)
                ?? (entry!.Value.ValueKind == JsonValueKind.Undefined ? "A value is required." : SettingValueValidator.Validate(definition, entry.Value));
            if (error is null && !seen.Add((key, scopeType, scopeId))) error = "Listed twice.";

            stored.TryGetValue((key, scopeType, scopeId), out var current);
            JsonElement? oldValue = current is null ? null : Parse(current.ValueJson);
            if (error is not null)
            {
                lines.Add(new PlatformSettingsImportLineDto(key, scopeType, scopeId, "rejected", oldValue, null, error));
                continue;
            }

            var value = SettingValueValidator.Normalize(definition!, entry!.Value);
            if (oldValue is { } existing && SettingValueValidator.AreEqual(existing, value))
            {
                lines.Add(new PlatformSettingsImportLineDto(key, scopeType, scopeId, "unchanged", oldValue, value, null));
                continue;
            }

            lines.Add(new PlatformSettingsImportLineDto(key, scopeType, scopeId, current is null ? "create" : "update", oldValue, value, null));
            plan.Add((definition!, scopeType, scopeId, value));
        }

        var rejected = lines.Count(l => l.Outcome == "rejected");
        var unchanged = lines.Count(l => l.Outcome == "unchanged");
        // All or nothing: a half-applied settings file is a configuration nobody chose.
        if (request.DryRun || rejected > 0 || plan.Count == 0)
            return Result.Success(new PlatformSettingsImportResultDto(false, plan.Count, unchanged, rejected, lines));

        var now = Now;
        var importer = new Actor(actor, user.GetEmail());
        foreach (var (definition, scopeType, scopeId, value) in plan)
        {
            var row = await _values.GetAsync(definition.Key, scopeType, scopeId, ct);
            await StageAsync(definition, importer, scopeType, scopeId, row, value, PlatformSettingChangeActions.Import, reason, correlationId, null, now, ct);
        }

        await _auditLog.AppendAsync(AuditEntry(AdminAuditPlatformSettingActions.Imported, "*", "Platform settings", null,
            reason!, actor, user.GetEmail(), correlationId, null,
            new Dictionary<string, object?> { ["changed"] = plan.Select(p => Describe(p.Definition, p.ScopeType, p.ScopeId)).ToList() }), ct);

        var saved = await SaveAsync(plan[0].Definition, ct);
        if (!saved.IsSuccess) return Result.Failure<PlatformSettingsImportResultDto>(saved.Error!, saved.ErrorCode);
        await PublishAsync(ct);
        return Result.Success(new PlatformSettingsImportResultDto(true, plan.Count, unchanged, 0, lines));
    }

    public async Task<bool> PublishAsync(CancellationToken ct = default)
    {
        var values = await _values.GetAllAsync(ct);
        var latest = await _changes.GetLatestPerKeyAsync(ct);
        var version = latest.Count == 0 ? 0 : latest.Values.Max(c => c.ChangedAt).Ticks;
        var published = await _publisher.PublishAsync(values, version, ct);
        (_localReader as PlatformSettingsReader)?.Invalidate();
        return published;
    }

    // ── Internals ───────────────────────────────────────────────────────────────────────────

    private sealed record Prepared(SettingDefinition Definition, Actor Actor, string ScopeType, string ScopeId, string? Reason);

    private sealed record Actor(Guid Id, string? Email);

    private async Task<Result<Prepared>> PrepareAsync(
        ClaimsPrincipal user, string key, string? scopeType, string? scopeId, string? reason, CancellationToken ct)
    {
        if (user.GetUserId() is not { } actor)
            return Result.Failure<Prepared>("Invalid or missing user identity.", ErrorCodes.Unauthorized);
        var definition = PlatformSettingsCatalog.Find(key);
        if (definition is null) return Result.Failure<Prepared>("Unknown setting.", ErrorCodes.NotFound);

        var (type, id, scopeError) = NormalizeScope(definition, scopeType, scopeId);
        if (scopeError is not null) return Result.Failure<Prepared>(scopeError, ErrorCodes.ValidationError);

        if (definition.RequiresSecurityPermission && !await _access.HasPermissionAsync(user, AdminPermissions.SettingsSecurity, ct))
            return Result.Failure<Prepared>("Security settings need the settings.security permission.", ErrorCodes.Forbidden);

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > MaxReasonLength })
            return Result.Failure<Prepared>($"A reason must be at most {MaxReasonLength} characters.", ErrorCodes.ValidationError);
        if (definition.Risky && (trimmed is null || trimmed.Length < MinReasonLength))
            return Result.Failure<Prepared>($"This setting is marked risky: give a reason of at least {MinReasonLength} characters.", ErrorCodes.ValidationError);

        return Result.Success(new Prepared(definition, new Actor(actor, user.GetEmail()), type, id, trimmed));
    }

    private static (string ScopeType, string ScopeId, string? Error) NormalizeScope(SettingDefinition? definition, string? scopeType, string? scopeId)
    {
        var type = string.IsNullOrWhiteSpace(scopeType) ? PlatformSettingScopeTypes.Platform : scopeType.Trim().ToLowerInvariant();
        var id = scopeId?.Trim() ?? string.Empty;
        switch (type)
        {
            case PlatformSettingScopeTypes.Platform:
                return (type, string.Empty, id.Length == 0 ? null : "A platform value takes no scope id.");
            case PlatformSettingScopeTypes.Plan:
                if (definition is not null && !definition.AllowsScope(SettingScopes.Plan))
                    return (type, id, "This setting cannot be overridden per plan.");
                id = id.ToLowerInvariant();
                return (type, id, PlanSlug.IsMatch(id) ? null : "A plan override needs a plan slug.");
            case PlatformSettingScopeTypes.Workspace:
                if (definition is not null && !definition.AllowsScope(SettingScopes.Workspace))
                    return (type, id, "This setting cannot be overridden per workspace.");
                return Guid.TryParse(id, out var workspaceId)
                    ? (type, workspaceId.ToString("D"), null)
                    : (type, id, "A workspace override needs a workspace id.");
            default:
                return (type, id, "Scope must be platform, plan or workspace.");
        }
    }

    private async Task<Result> ApplyAsync(
        SettingDefinition definition, Actor actor, string scopeType, string scopeId, PlatformSettingValue? row,
        JsonElement? value, string historyAction, string auditAction, string? reason, string? correlationId,
        Guid? revertOf, CancellationToken ct)
    {
        var (before, after) = await StageAsync(definition, actor, scopeType, scopeId, row, value, historyAction, reason, correlationId, revertOf, Now, ct);

        await _auditLog.AppendAsync(AuditEntry(auditAction, definition.Key, definition.Label,
            scopeType == PlatformSettingScopeTypes.Workspace && Guid.TryParse(scopeId, out var ws) ? ws : null,
            reason ?? $"{definition.Label}: {historyAction}.", actor.Id, actor.Email, correlationId,
            before, after), ct);

        var saved = await SaveAsync(definition, ct);
        if (!saved.IsSuccess) return saved;

        await PublishAsync(ct);
        return Result.Success();
    }

    /// <summary>Tracks the value change and its history row; nothing is saved until the caller saves.</summary>
    private async Task<(Dictionary<string, object?> Before, Dictionary<string, object?> After)> StageAsync(
        SettingDefinition definition, Actor actor, string scopeType, string scopeId, PlatformSettingValue? row,
        JsonElement? value, string historyAction, string? reason, string? correlationId, Guid? revertOf, DateTime now,
        CancellationToken ct)
    {
        var oldJson = row?.ValueJson;
        string? newJson = value is { } v ? JsonSerializer.Serialize(v) : null;
        int version;
        if (value is null)
        {
            if (row is not null) _values.Remove(row);
            version = 0;
        }
        else if (row is null)
        {
            version = 1;
            await _values.AddAsync(new PlatformSettingValue
            {
                SettingKey = definition.Key,
                ScopeType = scopeType,
                ScopeId = scopeId,
                ValueJson = newJson!,
                Version = version,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = actor.Id,
            }, ct);
        }
        else
        {
            row.ValueJson = newJson!;
            row.Version += 1;
            row.UpdatedAt = now;
            row.UpdatedBy = actor.Id;
            version = row.Version;
        }

        await _changes.AppendAsync(new PlatformSettingChange
        {
            Id = Guid.NewGuid(),
            SettingKey = definition.Key,
            ScopeType = scopeType,
            ScopeId = scopeId,
            Action = historyAction,
            OldValueJson = oldJson,
            NewValueJson = newJson,
            Version = version,
            Reason = reason,
            ChangedBy = actor.Id,
            ChangedByEmail = Bound(actor.Email, 320),
            ChangedAt = now,
            CorrelationId = Bound(correlationId, 100),
            RevertOf = revertOf,
        }, ct);

        var before = new Dictionary<string, object?> { ["scope"] = Describe(definition, scopeType, scopeId), ["value"] = Redact(definition, oldJson) };
        var after = new Dictionary<string, object?> { ["scope"] = Describe(definition, scopeType, scopeId), ["value"] = Redact(definition, newJson) };
        return (before, after);
    }

    private async Task<Result> SaveAsync(SettingDefinition definition, CancellationToken ct)
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(StaleMessage(definition), ErrorCodes.Conflict);
        }
        catch (DbUpdateException ex) when (PersistenceConflict.IsUniqueViolation(ex))
        {
            return Result.Failure(StaleMessage(definition), ErrorCodes.Conflict);
        }
    }

    private async Task<Result<PlatformSettingDto>> CurrentAsync(ClaimsPrincipal user, SettingDefinition definition, CancellationToken ct)
    {
        var access = await _access.GetAsync(user, ct);
        var canManage = access.Has(AdminPermissions.SettingsManage);
        var stored = await _values.GetAllAsync(ct);
        var latest = await _changes.GetLatestPerKeyAsync(ct);
        return Result.Success(ToDto(definition, stored, latest, canManage, canManage && access.Has(AdminPermissions.SettingsSecurity)));
    }

    private static PlatformSettingDto ToDto(
        SettingDefinition definition,
        IReadOnlyList<PlatformSettingValue> stored,
        IReadOnlyDictionary<string, PlatformSettingChange> latest,
        bool canManage,
        bool canSecurity)
    {
        var mine = stored.Where(v => v.SettingKey == definition.Key).ToList();
        var platform = mine.FirstOrDefault(v => v.ScopeType == PlatformSettingScopeTypes.Platform);
        var overrides = mine
            .Where(v => v.ScopeType != PlatformSettingScopeTypes.Platform)
            .OrderBy(v => v.ScopeType, StringComparer.Ordinal).ThenBy(v => v.ScopeId, StringComparer.Ordinal)
            .Select(v => new PlatformSettingScopedValueDto(v.ScopeType, v.ScopeId, Parse(v.ValueJson), v.Version, v.UpdatedAt, v.UpdatedBy))
            .ToList();
        latest.TryGetValue(definition.Key, out var last);

        var scopes = new List<string> { PlatformSettingScopeTypes.Platform };
        if (definition.AllowsScope(SettingScopes.Plan)) scopes.Add(PlatformSettingScopeTypes.Plan);
        if (definition.AllowsScope(SettingScopes.Workspace)) scopes.Add(PlatformSettingScopeTypes.Workspace);

        return new PlatformSettingDto(
            definition.Key,
            definition.Category,
            TypeName(definition.Type),
            definition.Label,
            definition.Description,
            definition.OwningService,
            scopes,
            definition.Unit,
            definition.Min,
            definition.Max,
            definition.AllowedValues,
            definition.MaxLength,
            definition.Pattern,
            definition.RequiresRestart,
            definition.Risky,
            definition.Sensitive,
            definition.RequiresSecurityPermission,
            definition.Default,
            platform is null ? null : Parse(platform.ValueJson),
            platform?.Version ?? 0,
            platform is not null,
            overrides,
            last?.ChangedAt,
            last is null ? null : new PlatformSettingActorDto(last.ChangedBy, last.ChangedByName, last.ChangedByEmail),
            canManage && (!definition.RequiresSecurityPermission || canSecurity));
    }

    public static string TypeName(SettingValueType type) => type switch
    {
        SettingValueType.Boolean => "boolean",
        SettingValueType.Integer => "integer",
        SettingValueType.Decimal => "decimal",
        SettingValueType.String => "string",
        SettingValueType.Enum => "enum",
        SettingValueType.StringList => "string_list",
        SettingValueType.FeatureFlag => "feature_flag",
        _ => "unknown",
    };

    private static PlatformSettingChangeDto ToChangeDto(PlatformSettingChange change)
    {
        var sensitive = PlatformSettingsCatalog.Find(change.SettingKey)?.Sensitive == true;
        return new PlatformSettingChangeDto(
            change.Id,
            change.SettingKey,
            change.ScopeType,
            change.ScopeId,
            change.Action,
            sensitive || change.OldValueJson is null ? null : Parse(change.OldValueJson),
            sensitive || change.NewValueJson is null ? null : Parse(change.NewValueJson),
            change.Version,
            change.Reason,
            new PlatformSettingActorDto(change.ChangedBy, change.ChangedByName, change.ChangedByEmail),
            change.ChangedAt,
            change.RevertOf,
            sensitive);
    }

    private WorkspaceAdminAction AuditEntry(
        string action, string entityKey, string label, Guid? workspaceId, string reason, Guid actor, string? email,
        string? correlationId, Dictionary<string, object?>? before, Dictionary<string, object?>? after)
        => new()
        {
            Id = Guid.NewGuid(),
            SourceService = AdminAuditSources.WorkspaceService,
            WorkspaceId = workspaceId,
            EntityType = AdminAuditEntityTypes.PlatformSetting,
            EntityId = null,
            EntityKey = Bound(entityKey, 100),
            EntityLabel = Bound(label, 200),
            Action = action,
            Reason = Bound(reason, MaxReasonLength)!,
            Result = AdminAuditResults.Succeeded,
            PerformedBy = actor,
            PerformedAt = Now,
            CorrelationId = Bound(correlationId, 100),
            ActorEmail = Bound(email, 320),
            BeforeSummary = before is null ? null : JsonSerializer.Serialize(before),
            AfterSummary = after is null ? null : JsonSerializer.Serialize(after),
        };

    private static string Describe(SettingDefinition definition, string scopeType, string scopeId)
        => scopeType == PlatformSettingScopeTypes.Platform ? definition.Key : $"{definition.Key}@{scopeType}:{scopeId}";

    private static object? Redact(SettingDefinition definition, string? json)
        => json is null ? null : definition.Sensitive ? "[redacted]" : Parse(json);

    private static string StaleMessage(SettingDefinition definition)
        => $"{definition.Label} was changed by someone else since you opened it. Reload to see the current value.";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? Bound(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}

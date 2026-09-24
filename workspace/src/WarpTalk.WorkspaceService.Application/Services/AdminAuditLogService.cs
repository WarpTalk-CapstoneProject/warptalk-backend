using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Helpers;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Application.Models;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

public class AdminAuditLogService : IAdminAuditLogService
{
    private const int MaxRangeDays = 366;
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxExportRows = 10_000;
    private const int ExportBatch = 500;
    public const int MaxSearchLength = 200;

    /// <summary>What <see cref="RecordAsync"/> writes for a blank reason — not something anybody said.</summary>
    public const string NoReasonPlaceholder = "(no reason given)";

    /// <summary>A page names few actors; looking up more than this many at once is a runaway page.</summary>
    private const int MaxDirectoryLookups = 50;

    private static readonly string[] AllowedResults =
    [
        AdminAuditResults.Succeeded,
        AdminAuditResults.Failed,
    ];

    /// <summary>Summary keys that name the subject, in the order a person would look for one.</summary>
    private static readonly string[] LabelKeys =
    [
        "name", "display_name", "plugin_name", "plan_name", "title", "term", "source_term",
        "invoice_number", "company_name", "email", "code", "plugin_key", "key", "slug",
    ];

    private readonly IAdminAuditLogRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<AdminAuditLogService> _logger;
    private readonly IAuthIdentityClient? _authIdentity;
    private readonly TimeProvider _time;

    public AdminAuditLogService(
        IAdminAuditLogRepository repository,
        IUnitOfWork unitOfWork,
        ILogger<AdminAuditLogService> logger,
        IAuthIdentityClient? authIdentity = null,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _authIdentity = authIdentity;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminAuditLogPageDto>> SearchAsync(AdminAuditLogQuery query, CancellationToken ct = default)
    {
        var parsed = Parse(query);
        if (!parsed.IsSuccess)
            return Result.Failure<AdminAuditLogPageDto>(parsed.Error!, parsed.ErrorCode!);

        var limit = NormalizeLimit(query.Limit);
        try
        {
            var rows = await _repository.SearchAsync(parsed.Value! with { Take = limit + 1 }, ct);
            var hasMore = rows.Count > limit;
            var pageRows = rows.Take(limit).ToList();
            var items = await EnrichAsync(pageRows, ct);
            var last = pageRows.LastOrDefault();
            var nextCursor = hasMore && last is not null ? EncodeCursor(last.PerformedAt, last.Id) : null;
            return Result.Success(new AdminAuditLogPageDto(items, nextCursor, hasMore));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin audit log search failed.");
            return Result.Failure<AdminAuditLogPageDto>(
                "An unexpected error occurred while querying the audit log.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminAuditLogEntryDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var row = await _repository.GetByIdAsync(id, ct);
            if (row is null)
                return Result.Failure<AdminAuditLogEntryDto>("No audit entry has that id.", ErrorCodes.NotFound);

            return Result.Success((await EnrichAsync([row], ct))[0]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin audit entry read failed. Id: {Id}", id);
            return Result.Failure<AdminAuditLogEntryDto>(
                "An unexpected error occurred while reading the audit entry.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminAuditLogFacetsDto>> GetFacetsAsync(CancellationToken ct = default)
    {
        try
        {
            var facets = await _repository.GetFacetsAsync(ct);
            var missing = facets.Actors
                .Where(actor => actor.Name is null || actor.Email is null)
                .Select(actor => actor.ActorId)
                .ToList();
            var users = await LookupUsersAsync(missing, ct);

            var actors = facets.Actors
                .Select(actor =>
                {
                    users.TryGetValue(actor.ActorId, out var user);
                    return new AdminAuditActorFacetDto(
                        actor.ActorId,
                        actor.Name ?? NullIfBlank(user?.FullName),
                        actor.Email ?? NullIfBlank(user?.Email),
                        actor.Count);
                })
                .ToList();

            return Result.Success(new AdminAuditLogFacetsDto(
                facets.Actions.Select(item => new AdminAuditFacetValueDto(item.Value, item.Count)).ToList(),
                facets.EntityTypes.Select(item => new AdminAuditFacetValueDto(item.Value, item.Count)).ToList(),
                facets.SourceServices.Select(item => new AdminAuditFacetValueDto(item.Value, item.Count)).ToList(),
                actors));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin audit facets failed.");
            return Result.Failure<AdminAuditLogFacetsDto>(
                "An unexpected error occurred while reading the audit filters.", ErrorCodes.InternalServerError);
        }
    }

    public async Task<Result<AdminAuditLogExport>> ExportCsvAsync(
        AdminAuditLogQuery query,
        AdminActorContext actor,
        CancellationToken ct = default)
    {
        var parsed = Parse(query with { Cursor = null });
        if (!parsed.IsSuccess)
            return Result.Failure<AdminAuditLogExport>(parsed.Error!, parsed.ErrorCode!);

        try
        {
            var search = parsed.Value!;
            var rows = new List<WorkspaceAdminAction>();
            var truncated = false;
            while (true)
            {
                var batch = await _repository.SearchAsync(search with { Take = ExportBatch }, ct);
                foreach (var row in batch)
                {
                    if (rows.Count == MaxExportRows)
                    {
                        truncated = true;
                        break;
                    }

                    rows.Add(row);
                }

                if (truncated || batch.Count < ExportBatch) break;
                var last = batch[^1];
                search = search with { AfterPerformedAt = last.PerformedAt, AfterId = last.Id };
            }

            var entries = new List<AdminAuditLogEntryDto>(rows.Count);
            foreach (var chunk in rows.Chunk(MaxLimit))
            {
                entries.AddRange(await EnrichAsync(chunk.ToList(), ct));
            }

            var now = _time.GetUtcNow().UtcDateTime;

            // Recorded before the file is handed over: the trail of who read the trail out of the
            // portal is part of the trail. An export that cannot be recorded is not returned.
            await _repository.AppendAsync(new WorkspaceAdminAction
            {
                Id = Guid.NewGuid(),
                SourceService = AdminAuditSources.WorkspaceService,
                Action = AdminAuditLogActions.Exported,
                EntityType = AdminAuditEntityTypes.AuditLog,
                PerformedBy = actor.ActorId,
                PerformedAt = now,
                CorrelationId = actor.CorrelationId,
                Reason = "CSV export from the audit log screen",
                Result = AdminAuditResults.Succeeded,
                AfterSummary = JsonSerializer.Serialize(DescribeExport(query, entries.Count, truncated)),
            }, ct);
            await _unitOfWork.SaveChangesAsync(ct);

            var fileName = $"warptalk-audit-log-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.csv";
            return Result.Success(new AdminAuditLogExport(AdminAuditCsv.Render(entries), fileName, entries.Count, truncated));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin audit log export failed.");
            return Result.Failure<AdminAuditLogExport>(
                "The export could not be produced, so no file was returned.", ErrorCodes.InternalServerError);
        }
    }

    // ── Append ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends an action recorded by another service. Idempotent on
    /// (source, correlation id, action, entity) so a retried call does not duplicate a row.
    /// </summary>
    public async Task<Result> RecordAsync(AdminActionRecordedEvent action, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(action.SourceService)
            || string.IsNullOrWhiteSpace(action.Action)
            || string.IsNullOrWhiteSpace(action.EntityType))
        {
            return Result.Failure(
                "source_service, action, and entity_type are required.", ErrorCodes.ValidationError);
        }

        var result = AllowedResults.Contains(action.Result, StringComparer.OrdinalIgnoreCase)
            ? action.Result.ToLowerInvariant()
            : AdminAuditResults.Succeeded;

        try
        {
            if (await _repository.ExistsAsync(
                    action.SourceService, action.CorrelationId, action.Action, action.EntityId, ct))
            {
                _logger.LogDebug(
                    "Skipping duplicate admin audit entry. Source: {Source}, CorrelationId: {CorrelationId}",
                    action.SourceService,
                    action.CorrelationId);
                return Result.Success();
            }

            await _repository.AppendAsync(
                new WorkspaceAdminAction
                {
                    Id = Guid.NewGuid(),
                    SourceService = action.SourceService,
                    Action = action.Action,
                    EntityType = action.EntityType,
                    EntityId = action.EntityId,
                    WorkspaceId = action.WorkspaceId,
                    PerformedBy = action.ActorId,
                    Reason = string.IsNullOrWhiteSpace(action.Reason) ? NoReasonPlaceholder : action.Reason,
                    Result = result,
                    PerformedAt = action.PerformedAt == default
                        ? DateTime.UtcNow
                        : action.PerformedAt.ToUniversalTime(),
                    CorrelationId = action.CorrelationId,
                    // Redacted again here: the publisher is expected to redact, but a secret in
                    // an append-only table with no DELETE grant is not removable afterwards.
                    BeforeSummary = Serialize(AdminAuditRedaction.Redact(action.BeforeSummary)),
                    AfterSummary = Serialize(AdminAuditRedaction.Redact(action.AfterSummary)),
                    ActorEmail = Bound(action.ActorEmail, 320),
                    ActorName = Bound(action.ActorName, 200),
                    EntityKey = Bound(action.EntityKey, 100),
                    EntityLabel = Bound(action.EntityLabel, 200),
                    ErrorMessage = result == AdminAuditResults.Failed ? Bound(action.ErrorMessage, 2000) : null,
                    IpAddress = Bound(action.IpAddress, 64),
                    UserAgent = Bound(action.UserAgent, 512),
                },
                ct);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to record admin audit entry. Source: {Source}, Action: {Action}",
                action.SourceService,
                action.Action);
            return Result.Failure("Failed to record the admin action.", ErrorCodes.InternalServerError);
        }
    }

    // ── Query parsing ───────────────────────────────────────────────────────────────────────

    public static int NormalizeLimit(int? limit) =>
        limit is null or <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>Validates the query and turns it into the repository's search. Take is set by the caller.</summary>
    public static Result<AdminAuditLogSearch> Parse(AdminAuditLogQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Result)
            && !AllowedResults.Contains(query.Result.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Result.Failure<AdminAuditLogSearch>(
                "Unknown result filter. Expected 'succeeded' or 'failed'.", ErrorCodes.ValidationError);
        }

        if (ValidateRange(query.From, query.To) is { } rangeError)
            return Result.Failure<AdminAuditLogSearch>(rangeError, ErrorCodes.ValidationError);

        var text = query.Q?.Trim();
        if (text is { Length: > MaxSearchLength })
            return Result.Failure<AdminAuditLogSearch>(
                $"Search text must be at most {MaxSearchLength} characters.", ErrorCodes.ValidationError);

        DateTime? afterAt = null;
        Guid? afterId = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!TryDecodeCursor(query.Cursor, out var at, out var id))
                return Result.Failure<AdminAuditLogSearch>("The cursor is not valid.", ErrorCodes.ValidationError);
            afterAt = at;
            afterId = id;
        }

        var subject = query.EntityId?.Trim();
        Guid? entityId = Guid.TryParse(subject, out var parsedEntity) ? parsedEntity : null;
        var entityKey = entityId is null && !string.IsNullOrWhiteSpace(subject) ? subject : null;

        return Result.Success(new AdminAuditLogSearch(
            Take: DefaultLimit,
            AfterPerformedAt: afterAt,
            AfterId: afterId,
            ActorId: query.ActorId,
            Action: NullIfBlank(query.Action?.Trim()),
            EntityType: NullIfBlank(query.EntityType?.Trim()),
            EntityId: entityId,
            EntityKey: entityKey,
            WorkspaceId: query.WorkspaceId,
            SourceService: NullIfBlank(query.SourceService?.Trim()),
            Result: NullIfBlank(query.Result?.Trim().ToLowerInvariant()),
            From: ToUtc(query.From),
            To: ToUtc(query.To),
            Text: NullIfBlank(text)));
    }

    /// <summary>Opaque to clients: base64url of "{ticks}:{id}" for the last row shown.</summary>
    public static string EncodeCursor(DateTime performedAt, Guid id)
    {
        var utc = DateTime.SpecifyKind(performedAt, DateTimeKind.Utc);
        var raw = Encoding.UTF8.GetBytes($"{utc.Ticks.ToString(CultureInfo.InvariantCulture)}:{id:N}");
        return Base64Url.EncodeToString(raw);
    }

    public static bool TryDecodeCursor(string cursor, out DateTime performedAt, out Guid id)
    {
        performedAt = default;
        id = default;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.Trim()));
            var parts = text.Split(':');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "N", out id))
            {
                return false;
            }

            performedAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shared by the admin and the workspace-scoped reads so both reject the same ranges.
    /// Returns the validation message, or null when the range is acceptable.
    /// </summary>
    public static string? ValidateRange(DateTime? from, DateTime? to)
    {
        if (from is { } f && to is { } t)
        {
            if (f >= t) return "'from' must be earlier than 'to'.";
            if ((t - f).TotalDays > MaxRangeDays) return $"Date range must not exceed {MaxRangeDays} days.";
        }

        return null;
    }

    public static DateTime? ToUtc(DateTime? value) => value?.ToUniversalTime();

    // ── Mapping ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Names what the stored rows only identify: actors and account subjects through the auth
    /// directory, workspaces through this service's own table. What cannot be named stays null.
    /// </summary>
    private async Task<IReadOnlyList<AdminAuditLogEntryDto>> EnrichAsync(
        IReadOnlyList<WorkspaceAdminAction> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var userIds = rows
            .Where(row => row.ActorName is null || row.ActorEmail is null)
            .Select(row => row.PerformedBy)
            .Concat(rows
                .Where(row => row.EntityType == AdminAuditEntityTypes.User && row.EntityLabel is null && row.EntityId is not null)
                .Select(row => row.EntityId!.Value))
            .Distinct()
            .ToList();
        var users = await LookupUsersAsync(userIds, ct);

        var workspaceIds = rows
            .Select(row => row.WorkspaceId)
            .Concat(rows.Where(row => row.EntityType == AdminAuditEntityTypes.Workspace).Select(row => row.EntityId))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var workspaces = await _repository.GetWorkspaceNamesAsync(workspaceIds, ct);

        return rows.Select(row => ToDto(row, users, workspaces)).ToList();
    }

    private async Task<Dictionary<Guid, User>> LookupUsersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var found = new Dictionary<Guid, User>();
        if (_authIdentity is null || ids.Count == 0) return found;

        var lookups = ids.Take(MaxDirectoryLookups)
            .Select(async id => (Id: id, User: await _authIdentity.GetUserByIdAsync(id, ct)));
        foreach (var (id, user) in await Task.WhenAll(lookups))
        {
            if (user is not null) found[id] = user;
        }

        return found;
    }

    /// <summary>The stored row alone, with nothing looked up — what the tenant-scoped read reuses.</summary>
    public static AdminAuditLogEntryDto ToDto(WorkspaceAdminAction row) =>
        ToDto(row, new Dictionary<Guid, User>(), new Dictionary<Guid, AdminAuditWorkspaceName>());

    public static AdminAuditLogEntryDto ToDto(
        WorkspaceAdminAction row,
        IReadOnlyDictionary<Guid, User> users,
        IReadOnlyDictionary<Guid, AdminAuditWorkspaceName> workspaces)
    {
        var before = Deserialize(row.BeforeSummary);
        var after = Deserialize(row.AfterSummary);

        users.TryGetValue(row.PerformedBy, out var actorUser);
        AdminAuditWorkspaceName? workspace = null;
        if (row.WorkspaceId is { } workspaceId) workspaces.TryGetValue(workspaceId, out workspace);

        return new AdminAuditLogEntryDto(
            row.Id,
            DateTime.SpecifyKind(row.PerformedAt, DateTimeKind.Utc),
            row.SourceService,
            row.Action,
            new AdminAuditActorDto(
                row.PerformedBy,
                row.ActorName ?? NullIfBlank(actorUser?.FullName),
                row.ActorEmail ?? NullIfBlank(actorUser?.Email)),
            new AdminAuditEntityDto(
                row.EntityType,
                row.EntityId,
                row.EntityKey,
                LabelOf(row, users, workspaces, before, after),
                row.WorkspaceId,
                workspace?.Name,
                workspace?.Slug),
            row.Reason == NoReasonPlaceholder ? null : NullIfBlank(row.Reason),
            row.Result,
            row.ErrorMessage,
            new AdminAuditRequestDto(row.CorrelationId, row.IpAddress, row.UserAgent),
            before,
            after);
    }

    private static string? LabelOf(
        WorkspaceAdminAction row,
        IReadOnlyDictionary<Guid, User> users,
        IReadOnlyDictionary<Guid, AdminAuditWorkspaceName> workspaces,
        IReadOnlyDictionary<string, string?>? before,
        IReadOnlyDictionary<string, string?>? after)
    {
        if (!string.IsNullOrWhiteSpace(row.EntityLabel)) return row.EntityLabel;

        if (row.EntityId is { } entityId)
        {
            if (row.EntityType == AdminAuditEntityTypes.Workspace && workspaces.TryGetValue(entityId, out var workspace))
                return workspace.Name;
            if (row.EntityType == AdminAuditEntityTypes.User && users.TryGetValue(entityId, out var user))
                return NullIfBlank(user.FullName) ?? NullIfBlank(user.Email);
        }

        foreach (var key in LabelKeys)
        {
            if (after?.GetValueOrDefault(key) is { Length: > 0 } fromAfter) return fromAfter;
            if (before?.GetValueOrDefault(key) is { Length: > 0 } fromBefore) return fromBefore;
        }

        return null;
    }

    private static Dictionary<string, string?> DescribeExport(AdminAuditLogQuery query, int rows, bool truncated)
    {
        var summary = new Dictionary<string, string?>
        {
            ["rows"] = rows.ToString(CultureInfo.InvariantCulture),
            ["truncated"] = truncated ? "true" : "false",
        };
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) summary[key] = value.Trim();
        }

        Add("from", query.From?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add("to", query.To?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add("actor_id", query.ActorId?.ToString());
        Add("action", query.Action);
        Add("entity_type", query.EntityType);
        Add("entity_id", query.EntityId);
        Add("workspace_id", query.WorkspaceId?.ToString());
        Add("source_service", query.SourceService);
        Add("result", query.Result);
        Add("q", query.Q);
        return summary;
    }

    private static string? Serialize(IReadOnlyDictionary<string, string?>? summary) =>
        summary is null || summary.Count == 0 ? null : JsonSerializer.Serialize(summary);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>
    /// The stored summaries are jsonb objects. A value that is not a string (a number written by
    /// another producer) is shown as its JSON text rather than dropping the whole summary.
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var parsed = document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                });
            return AdminAuditRedaction.Redact(parsed);
        }
        catch (JsonException)
        {
            // A malformed summary must not take down the whole page of audit entries.
            return null;
        }
    }
}

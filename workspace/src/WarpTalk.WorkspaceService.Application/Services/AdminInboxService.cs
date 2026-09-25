using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;
using WarpTalk.Shared.Contracts.Admin;
using WarpTalk.Shared.Events;
using WarpTalk.WorkspaceService.Application.DTOs.Admin;
using WarpTalk.WorkspaceService.Application.Interfaces;
using WarpTalk.WorkspaceService.Domain.Entities;
using WarpTalk.WorkspaceService.Domain.Interfaces;

namespace WarpTalk.WorkspaceService.Application.Services;

/// <summary>A pending-work source the inbox reads, and the permission that lets a staff member see it.</summary>
public sealed record AdminInboxSourceDefinition(string Source, string Permission);

/// <summary>Reads one source's items with the caller's own credentials, within a timeout. Never throws.</summary>
public interface IAdminInboxSourceClient
{
    IReadOnlyList<AdminInboxSourceDefinition> Sources { get; }

    Task<AdminInboxSourceResult> ReadAsync(string source, CancellationToken ct);
}

public interface IAdminInboxService
{
    Task<Result<AdminInboxDto>> GetAsync(ClaimsPrincipal user, bool refresh, CancellationToken ct = default);
    Task<Result<AdminInboxSummaryDto>> GetSummaryAsync(ClaimsPrincipal user, CancellationToken ct = default);
    Task<Result<AdminInboxItemDto>> AssignAsync(ClaimsPrincipal user, AdminInboxAssignRequest request, string? correlationId, CancellationToken ct = default);
    Task<Result<AdminInboxItemDto>> SnoozeAsync(ClaimsPrincipal user, AdminInboxSnoozeRequest request, string? correlationId, CancellationToken ct = default);
    Task<Result<AdminInboxItemDto>> MarkDoneAsync(ClaimsPrincipal user, AdminInboxKeyRequest request, string? correlationId, CancellationToken ct = default);
    Task<Result<AdminInboxItemDto>> ReopenAsync(ClaimsPrincipal user, AdminInboxKeyRequest request, string? correlationId, CancellationToken ct = default);
    Task<Result<AdminInboxNoteDto>> AddNoteAsync(ClaimsPrincipal user, AdminInboxNoteRequest request, string? correlationId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AdminInboxNoteDto>>> GetNotesAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// The pending-work inbox (G12): one queue of everything waiting on platform staff, read LIVE from the
/// services that own each thing — billing (leads, invoices, disputes, trials, renewals, suspensions),
/// providers (incidents, 402 quota), operating expenses due, auth (staff invitations), notification
/// (announcements to review, stale drafts, failed broadcasts) and this service's own outbox (dead letters).
///
/// Sources are read in parallel with the caller's own token, each within a timeout, and only those the
/// caller's role can see; a source that fails is reported as unavailable and the rest still render. The
/// raw answers are cached per staff member for <see cref="CacheSeconds"/> seconds, which is what lets the
/// sidebar badge poll without fanning out on every tick. Only triage (assignment, snooze, notes, manual
/// done) is stored — in this service — and merged in on every read.
/// </summary>
public sealed class AdminInboxService : IAdminInboxService
{
    public const int CacheSeconds = 30;
    public const int MaxNoteLength = 4000;
    public const int MaxNotes = 200;
    public const int MaxKeyLength = 200;
    public const int MaxSnoozeDays = 90;
    private const int MaxReasonLength = 500;

    private readonly IAdminInboxSourceClient _client;
    private readonly IStaffAccessResolver _access;
    private readonly IAdminInboxStateRepository _states;
    private readonly IAdminInboxNoteRepository _notes;
    private readonly IAdminAuditLogRepository _auditLog;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuthIdentityClient _identity;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminInboxService> _logger;
    private readonly TimeProvider _time;

    public AdminInboxService(
        IAdminInboxSourceClient client,
        IStaffAccessResolver access,
        IAdminInboxStateRepository states,
        IAdminInboxNoteRepository notes,
        IAdminAuditLogRepository auditLog,
        IUnitOfWork unitOfWork,
        IAuthIdentityClient identity,
        IMemoryCache cache,
        ILogger<AdminInboxService> logger,
        TimeProvider? time = null)
    {
        _client = client;
        _access = access;
        _states = states;
        _notes = notes;
        _auditLog = auditLog;
        _unitOfWork = unitOfWork;
        _identity = identity;
        _cache = cache;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminInboxDto>> GetAsync(ClaimsPrincipal user, bool refresh, CancellationToken ct = default)
    {
        if (user.GetUserIdOrNull() is not { } viewer)
            return Result.Failure<AdminInboxDto>("Invalid or missing user identity.", ErrorCodes.Unauthorized);

        var raw = await ReadSourcesAsync(user, viewer, refresh, ct);
        var items = await MergeAsync(raw, ct);
        return Result.Success(new AdminInboxDto(Now, viewer, items, raw.Statuses, Count(items, viewer)));
    }

    public async Task<Result<AdminInboxSummaryDto>> GetSummaryAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.GetUserIdOrNull() is not { } viewer)
            return Result.Failure<AdminInboxSummaryDto>("Invalid or missing user identity.", ErrorCodes.Unauthorized);

        var raw = await ReadSourcesAsync(user, viewer, refresh: false, ct);
        var items = await MergeAsync(raw, ct, withNames: false);
        return Result.Success(new AdminInboxSummaryDto(
            Now, Count(items, viewer), raw.Statuses.Count(s => s.Status == SourceStatuses.Unavailable)));
    }

    public async Task<Result<IReadOnlyList<AdminInboxNoteDto>>> GetNotesAsync(string key, CancellationToken ct = default)
    {
        if (!ValidKey(key)) return Result.Failure<IReadOnlyList<AdminInboxNoteDto>>("Invalid item key.", ErrorCodes.ValidationError);
        var notes = await _notes.GetForItemAsync(key, MaxNotes, ct);
        var names = await NamesAsync(notes.Select(n => n.AuthorId), ct);
        return Result.Success<IReadOnlyList<AdminInboxNoteDto>>(notes
            .Select(n => new AdminInboxNoteDto(n.Id, n.ItemKey, n.Body, n.AuthorId, names.GetValueOrDefault(n.AuthorId), Utc(n.CreatedAt)))
            .ToList());
    }

    // ── Triage ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminInboxItemDto>> AssignAsync(ClaimsPrincipal user, AdminInboxAssignRequest request, string? correlationId, CancellationToken ct = default)
        => await TriageAsync(user, request?.Key, AdminAuditInboxActions.Assigned, correlationId, ct, (state, item, actor, now) =>
        {
            if (request!.AssigneeId == Guid.Empty) return "Choose someone to assign it to.";
            state.AssigneeId = request.AssigneeId;
            state.AssignedBy = request.AssigneeId is null ? null : actor;
            state.AssignedAt = request.AssigneeId is null ? null : now;
            return null;
        }, state => $"assignee={state.AssigneeId?.ToString() ?? "none"}");

    public async Task<Result<AdminInboxItemDto>> SnoozeAsync(ClaimsPrincipal user, AdminInboxSnoozeRequest request, string? correlationId, CancellationToken ct = default)
        => await TriageAsync(user, request?.Key, AdminAuditInboxActions.Snoozed, correlationId, ct, (state, item, actor, now) =>
        {
            if (request!.Until is { } until)
            {
                var utc = until.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(until, DateTimeKind.Utc) : until.ToUniversalTime();
                if (utc <= now) return "Snooze until a time in the future.";
                if (utc > now.AddDays(MaxSnoozeDays)) return $"Snooze for at most {MaxSnoozeDays} days.";
                state.SnoozedUntil = utc;
                state.SnoozedBy = actor;
            }
            else
            {
                state.SnoozedUntil = null;
                state.SnoozedBy = null;
            }

            return null;
        }, state => $"snoozed_until={state.SnoozedUntil?.ToString("O") ?? "none"}");

    public async Task<Result<AdminInboxItemDto>> MarkDoneAsync(ClaimsPrincipal user, AdminInboxKeyRequest request, string? correlationId, CancellationToken ct = default)
        => await TriageAsync(user, request?.Key, AdminAuditInboxActions.Done, correlationId, ct, (state, item, actor, now) =>
        {
            // An item that resolves itself is closed by doing the work in its source, not here: a manual
            // "done" on it would hide work that is still waiting.
            if (item.NaturalCompletion) return "This item closes by itself once it is handled on its page.";
            state.DoneAt = now;
            state.DoneBy = actor;
            return null;
        }, _ => "done", conflictOnError: true);

    public async Task<Result<AdminInboxItemDto>> ReopenAsync(ClaimsPrincipal user, AdminInboxKeyRequest request, string? correlationId, CancellationToken ct = default)
        => await TriageAsync(user, request?.Key, AdminAuditInboxActions.Reopened, correlationId, ct, (state, item, actor, now) =>
        {
            state.DoneAt = null;
            state.DoneBy = null;
            state.SnoozedUntil = null;
            state.SnoozedBy = null;
            return null;
        }, _ => "reopened");

    public async Task<Result<AdminInboxNoteDto>> AddNoteAsync(ClaimsPrincipal user, AdminInboxNoteRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (user.GetUserIdOrNull() is not { } actor)
            return Result.Failure<AdminInboxNoteDto>("Invalid or missing user identity.", ErrorCodes.Unauthorized);
        if (!ValidKey(request?.Key)) return Result.Failure<AdminInboxNoteDto>("Invalid item key.", ErrorCodes.ValidationError);
        var body = request!.Body?.Trim() ?? string.Empty;
        if (body.Length == 0) return Result.Failure<AdminInboxNoteDto>("A note cannot be empty.", ErrorCodes.ValidationError);
        if (body.Length > MaxNoteLength)
            return Result.Failure<AdminInboxNoteDto>($"A note must be at most {MaxNoteLength} characters.", ErrorCodes.ValidationError);

        var item = await FindItemAsync(user, actor, request.Key, ct);
        if (item is null) return Result.Failure<AdminInboxNoteDto>("That item is no longer in your inbox.", ErrorCodes.NotFound);

        var now = Now;
        var note = new AdminInboxNote { Id = Guid.NewGuid(), ItemKey = request.Key, Body = body, AuthorId = actor, CreatedAt = now };
        await _notes.AppendAsync(note, ct);
        await _auditLog.AppendAsync(AuditEntry(item, AdminAuditInboxActions.NoteAdded,
            body.Length > MaxReasonLength ? body[..MaxReasonLength] : body, actor, now, correlationId, null), ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var author = await SafeUserAsync(actor, ct);
        return Result.Success(new AdminInboxNoteDto(note.Id, note.ItemKey, note.Body, actor, author, now));
    }

    private async Task<Result<AdminInboxItemDto>> TriageAsync(
        ClaimsPrincipal user,
        string? key,
        string action,
        string? correlationId,
        CancellationToken ct,
        Func<AdminInboxItemState, AdminInboxSourceItem, Guid, DateTime, string?> apply,
        Func<AdminInboxItemState, string> describe,
        bool conflictOnError = false)
    {
        if (user.GetUserIdOrNull() is not { } actor)
            return Result.Failure<AdminInboxItemDto>("Invalid or missing user identity.", ErrorCodes.Unauthorized);
        if (!ValidKey(key)) return Result.Failure<AdminInboxItemDto>("Invalid item key.", ErrorCodes.ValidationError);

        // Only an item the caller can see right now can be triaged: this is what keeps a Support member
        // from snoozing a finance item they are not allowed to read.
        var item = await FindItemAsync(user, actor, key!, ct);
        if (item is null) return Result.Failure<AdminInboxItemDto>("That item is no longer in your inbox.", ErrorCodes.NotFound);

        var now = Now;
        var state = await _states.GetAsync(key!, ct);
        var isNew = state is null;
        state ??= new AdminInboxItemState { ItemKey = key!, ItemType = item.Item.Type, CreatedAt = now };
        var before = isNew ? null : describe(state);

        var error = apply(state, item, actor, now);
        if (error is not null)
            return Result.Failure<AdminInboxItemDto>(error, conflictOnError ? ErrorCodes.Conflict : ErrorCodes.ValidationError);

        state.UpdatedAt = now;
        state.UpdatedBy = actor;
        if (isNew) await _states.AddAsync(state, ct);
        await _auditLog.AppendAsync(AuditEntry(item, action, describe(state), actor, now, correlationId, before), ct);
        await _unitOfWork.SaveChangesAsync(ct);

        var notes = await _notes.SummarizeAsync([key!], ct);
        var names = await NamesAsync(state.AssigneeId is { } a ? [a] : [], ct);
        return Result.Success(ToDto(item, state, notes, names, now));
    }

    // ── Sources ───────────────────────────────────────────────────────────────────────────────

    public static class SourceStatuses
    {
        public const string Ok = "ok";
        public const string Unavailable = "unavailable";
        public const string Forbidden = "forbidden";
    }

    /// <summary>An item as a source gave it, with the source's name.</summary>
    public sealed record AdminInboxSourceItem(string Source, AdminInboxItem Item)
    {
        public bool NaturalCompletion => Item.NaturalCompletion;
    }

    private sealed record RawInbox(IReadOnlyList<AdminInboxSourceItem> Items, IReadOnlyList<AdminInboxSourceStatusDto> Statuses);

    private async Task<RawInbox> ReadSourcesAsync(ClaimsPrincipal user, Guid viewer, bool refresh, CancellationToken ct)
    {
        var cacheKey = $"admin-inbox:{viewer:N}";
        if (!refresh && _cache.TryGetValue(cacheKey, out RawInbox? cached) && cached is not null) return cached;

        var reads = _client.Sources.Select(async definition =>
        {
            bool allowed;
            try
            {
                allowed = await _access.HasPermissionAsync(user, definition.Permission, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Inbox could not resolve permission {Permission}; leaving {Source} out.", definition.Permission, definition.Source);
                return new AdminInboxSourceResult(definition.Source, null, SourceStatuses.Unavailable, 0, "Permission check failed.");
            }

            return allowed
                ? await _client.ReadAsync(definition.Source, ct)
                : new AdminInboxSourceResult(definition.Source, null, SourceStatuses.Forbidden, 0, null);
        });
        var results = await Task.WhenAll(reads);

        var items = new List<AdminInboxSourceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            foreach (var item in result.Response?.Items ?? [])
            {
                if (ValidKey(item.Key) && seen.Add(item.Key)) items.Add(new AdminInboxSourceItem(result.Source, item));
            }
        }

        var statuses = results
            .Select(r => new AdminInboxSourceStatusDto(
                r.Source, r.Status, r.Response?.Items.Count ?? 0, r.Response?.Truncated ?? false, r.DurationMs, r.Error))
            .ToList();
        var raw = new RawInbox(items, statuses);
        _cache.Set(cacheKey, raw, TimeSpan.FromSeconds(CacheSeconds));
        return raw;
    }

    private async Task<AdminInboxSourceItem?> FindItemAsync(ClaimsPrincipal user, Guid viewer, string key, CancellationToken ct)
    {
        var raw = await ReadSourcesAsync(user, viewer, refresh: false, ct);
        var item = raw.Items.FirstOrDefault(i => i.Item.Key == key);
        if (item is not null) return item;
        // The cache may predate the item: look once more, fresh.
        raw = await ReadSourcesAsync(user, viewer, refresh: true, ct);
        return raw.Items.FirstOrDefault(i => i.Item.Key == key);
    }

    private async Task<IReadOnlyList<AdminInboxItemDto>> MergeAsync(RawInbox raw, CancellationToken ct, bool withNames = true)
    {
        var keys = raw.Items.Select(i => i.Item.Key).ToList();
        var states = (await _states.GetManyAsync(keys, ct)).ToDictionary(s => s.ItemKey, StringComparer.Ordinal);
        var notes = await _notes.SummarizeAsync(keys, ct);
        var names = withNames
            ? await NamesAsync(states.Values.Where(s => s.AssigneeId.HasValue).Select(s => s.AssigneeId!.Value), ct)
            : new Dictionary<Guid, string>();
        var now = Now;

        return raw.Items
            .Select(item => ToDto(item, states.GetValueOrDefault(item.Item.Key), notes, names, now))
            .OrderByDescending(i => AdminInbox.Priorities.Rank(i.Priority))
            .ThenBy(i => i.DueAt ?? DateTime.MaxValue)
            .ThenBy(i => i.OccurredAt)
            .ToList();
    }

    private static AdminInboxItemDto ToDto(
        AdminInboxSourceItem source,
        AdminInboxItemState? state,
        IReadOnlyDictionary<string, (int Count, DateTime LastAt)> notes,
        IReadOnlyDictionary<Guid, string> names,
        DateTime now)
    {
        var item = source.Item;
        var snoozed = state?.SnoozedUntil is { } until && Utc(until) > now;
        var done = state?.DoneAt is not null && !item.NaturalCompletion;
        var noteSummary = notes.TryGetValue(item.Key, out var n) ? n : ((int Count, DateTime LastAt)?)null;
        var triage = new AdminInboxTriageDto(
            state?.AssigneeId,
            state?.AssigneeId is { } a ? names.GetValueOrDefault(a) : null,
            state?.AssignedAt is { } at ? Utc(at) : null,
            snoozed ? Utc(state!.SnoozedUntil!.Value) : null,
            done ? Utc(state!.DoneAt!.Value) : null,
            done ? state!.DoneBy : null,
            noteSummary?.Count ?? 0,
            noteSummary is { } s ? Utc(s.LastAt) : null);

        return new AdminInboxItemDto(
            item.Key,
            source.Source,
            item.Type,
            item.Title,
            item.Detail,
            item.WorkspaceId,
            item.Customer,
            Utc(item.OccurredAt),
            item.DueAt is { } due ? Utc(due) : null,
            AdminInbox.Priorities.All.Contains(item.Priority) ? item.Priority : AdminInbox.Priorities.Normal,
            SafeHref(item.Href),
            item.NaturalCompletion,
            item.Amount,
            item.Currency,
            item.DueAt is { } d && Utc(d) < now,
            snoozed,
            done,
            triage);
    }

    public static AdminInboxCountsDto Count(IReadOnlyList<AdminInboxItemDto> items, Guid viewer)
    {
        var open = items.Where(i => !i.Done && !i.Snoozed).ToList();
        return new AdminInboxCountsDto(
            open.Count,
            open.Count(i => i.Triage.AssigneeId == viewer),
            open.Count(i => i.Triage.AssigneeId is null),
            open.Count(i => i.Overdue),
            items.Count(i => i.Snoozed && !i.Done),
            items.Count(i => i.Done));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Only a path inside the admin portal: a source cannot send staff anywhere else.</summary>
    public static string SafeHref(string? href)
        => href is not null && href.StartsWith("/admin", StringComparison.Ordinal) && !href.StartsWith("//", StringComparison.Ordinal)
            ? href
            : "/admin/inbox";

    public static bool ValidKey(string? key)
        => !string.IsNullOrWhiteSpace(key) && key.Length <= MaxKeyLength && key.All(c => c > ' ' && c < 127);

    private WorkspaceAdminAction AuditEntry(
        AdminInboxSourceItem item, string action, string after, Guid actor, DateTime at, string? correlationId, string? before)
        => new()
        {
            Id = Guid.NewGuid(),
            SourceService = AdminAuditSources.WorkspaceService,
            WorkspaceId = item.Item.WorkspaceId,
            EntityType = AdminAuditEntityTypes.InboxItem,
            EntityId = null,
            EntityKey = item.Item.Key,
            EntityLabel = item.Item.Title.Length > 200 ? item.Item.Title[..200] : item.Item.Title,
            Action = action,
            Reason = after.Length > MaxReasonLength ? after[..MaxReasonLength] : after,
            Result = AdminAuditResults.Succeeded,
            PerformedBy = actor,
            PerformedAt = at,
            CorrelationId = correlationId,
            BeforeSummary = before is null ? null : JsonSerializer.Serialize(new Dictionary<string, string?> { ["state"] = before }),
            AfterSummary = JsonSerializer.Serialize(new Dictionary<string, string?> { ["state"] = after }),
        };

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var id in ids.Distinct())
        {
            if (await SafeUserAsync(id, ct) is { } name) names[id] = name;
        }

        return names;
    }

    private async Task<string?> SafeUserAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var user = await _identity.GetUserByIdAsync(id, ct);
            return string.IsNullOrWhiteSpace(user?.FullName) ? user?.Email : user.FullName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Inbox could not resolve the name of {UserId}.", id);
            return null;
        }
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal static class InboxClaimsExtensions
{
    public static Guid? GetUserIdOrNull(this ClaimsPrincipal user) => WarpTalk.Shared.Extensions.ClaimsPrincipalExtensions.GetUserId(user);
}

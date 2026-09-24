using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.DTOs.Common;
using WarpTalk.NotificationService.Application.Helpers.Announcements;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Application.Services.EmailCms;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.NotificationService.Domain.Rules;
using WarpTalk.Shared;
using WarpTalk.Shared.Authorization;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// The announcements CMS, and the reads and events the app makes to show them.
///
/// Lifecycle: DRAFT → (publish now | schedule) → PUBLISHED → ENDED (its window closed) → ARCHIVED.
/// Only DRAFT / PUBLISHED / ARCHIVED are stored; scheduled and ended are PUBLISHED read against
/// the clock (<see cref="AnnouncementLifecycle"/>), so a schedule takes effect with no worker.
/// Only drafts can be deleted. Every admin write is recorded in the platform audit log by the controller
/// ([AdminAudited]), before it commits.
/// </summary>
public sealed class AnnouncementService : IAnnouncementService
{
    private const int MaxPageSize = 100;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IViewerAudienceResolver _viewerAudience;
    private readonly TimeProvider _time;
    private readonly ILogger<AnnouncementService> _logger;

    public AnnouncementService(
        IUnitOfWork unitOfWork,
        IViewerAudienceResolver viewerAudience,
        ILogger<AnnouncementService> logger,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _viewerAudience = viewerAudience;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Admin reads ──────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminAnnouncementPageDto>> ListAsync(AdminAnnouncementListQuery query, CancellationToken ct = default)
    {
        var status = Upper(query.Status);
        if (status is not null && !AnnouncementConstants.EffectiveStatuses.Contains(status))
            return Invalid<AdminAnnouncementPageDto>($"Status must be one of {string.Join(", ", AnnouncementConstants.EffectiveStatuses)}.");
        var type = Upper(query.Type);
        if (type is not null && !AnnouncementConstants.Types.Contains(type))
            return Invalid<AdminAnnouncementPageDto>($"Type must be one of {string.Join(", ", AnnouncementConstants.Types)}.");
        var placement = Upper(query.Placement);
        if (placement is not null && !AnnouncementConstants.Placements.Contains(placement))
            return Invalid<AdminAnnouncementPageDto>($"Placement must be one of {string.Join(", ", AnnouncementConstants.Placements)}.");
        var sort = (query.Sort ?? "updated").Trim().ToLowerInvariant();
        if (sort is not ("updated" or "created" or "priority" or "title" or "starts"))
            return Invalid<AdminAnnouncementPageDto>("Sort must be updated, created, priority, title or starts.");
        var descending = !string.Equals(query.Order, "asc", StringComparison.OrdinalIgnoreCase);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var now = Now;

        var filter = new AnnouncementFilter(page, pageSize, status, search, type, placement, sort, descending);
        var repository = _unitOfWork.AnnouncementRepository;
        var (items, total) = await repository.GetPageAsync(filter, now, ct);
        var counts = await repository.CountByEffectiveStatusAsync(now, filter, ct);

        return Result.Success(new AdminAnnouncementPageDto(
            items.Select(item => ToAdminDto(item, now)).ToList(), total, page, pageSize, counts));
    }

    public async Task<Result<AdminAnnouncementDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        return announcement is null ? NotFound<AdminAnnouncementDto>() : Result.Success(ToAdminDto(announcement, Now));
    }

    public async Task<Result<AnnouncementAnalyticsDto>> GetAnalyticsAsync(Guid id, int days, CancellationToken ct = default)
    {
        if (await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct) is null) return NotFound<AnnouncementAnalyticsDto>();
        days = Math.Clamp(days, 1, 365);

        var totals = await _unitOfWork.AnnouncementViewerStateRepository.GetTotalsAsync(id, ct);
        var since = DateOnly.FromDateTime(Now).AddDays(-(days - 1));
        var rows = await _unitOfWork.AnnouncementDailyStatRepository.ListSinceAsync(id, since, ct);
        var daily = Enumerable.Range(0, days)
            .Select(offset => since.AddDays(offset))
            .Select(day =>
            {
                var row = rows.FirstOrDefault(r => r.Day == day);
                return new AnnouncementAnalyticsDayDto(day, row?.Impressions ?? 0, row?.Dismissals ?? 0, row?.CtaClicks ?? 0, row?.SecondaryClicks ?? 0);
            })
            .ToList();

        double Rate(int part) => totals.UniqueViewers == 0 ? 0 : Math.Round((double)part / totals.UniqueViewers, 4);
        return Result.Success(new AnnouncementAnalyticsDto(
            days,
            new AnnouncementAnalyticsTotalsDto(
                totals.UniqueViewers, totals.Impressions, totals.Dismissals, totals.UniqueCtaClickers,
                totals.CtaClicks, totals.SecondaryClicks, Rate(totals.UniqueCtaClickers), Rate(totals.Dismissals)),
            daily));
    }

    // ── Admin writes ─────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminAnnouncementDto>> CreateAsync(AdminActorContext actor, UpsertAnnouncementRequest request, CancellationToken ct = default)
    {
        var normalized = AnnouncementRules.Normalize(request);
        var error = AnnouncementRules.Validate(normalized);
        if (error is not null) return Invalid<AdminAnnouncementDto>(error);

        var now = Now;
        var announcement = new Announcement
        {
            Id = Guid.CreateVersion7(),
            Status = AnnouncementConstants.StatusDraft,
            CreatedBy = actor.ActorId,
            CreatedAt = now,
        };
        Apply(announcement, normalized, actor.ActorId, now);

        await _unitOfWork.AnnouncementRepository.AddAsync(announcement, ct);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> UpdateAsync(AdminActorContext actor, Guid id, UpsertAnnouncementRequest request, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();
        if (announcement.Status == AnnouncementConstants.StatusArchived)
            return InvalidState<AdminAnnouncementDto>("An archived announcement cannot be edited. Move it back to drafts first.");

        var normalized = AnnouncementRules.Normalize(request);
        var error = AnnouncementRules.Validate(normalized);
        if (error is not null) return Invalid<AdminAnnouncementDto>(error);

        var now = Now;
        Apply(announcement, normalized, actor.ActorId, now);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> PublishAsync(AdminActorContext actor, Guid id, PublishAnnouncementRequest request, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();

        var now = Now;
        var startsAt = AnnouncementRules.AsUtc(request.StartsAt);
        if (startsAt is { } scheduled && scheduled <= now)
            return Invalid<AdminAnnouncementDto>("A scheduled start must be in the future. To show it now, publish it now instead.");

        var effectiveStart = startsAt ?? now;
        if (announcement.EndsAt is { } endsAt && endsAt <= effectiveStart)
            return Invalid<AdminAnnouncementDto>("Its end time would have passed before it starts showing. Move the end time or clear it.");

        announcement.Status = AnnouncementConstants.StatusPublished;
        // Publish now clears a future start rather than keeping it: "now" has to mean now.
        announcement.StartsAt = startsAt;
        announcement.PublishedAt = now;
        announcement.PublishedBy = actor.ActorId;
        announcement.ArchivedAt = null;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> UnpublishAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();
        if (announcement.Status == AnnouncementConstants.StatusDraft) return InvalidState<AdminAnnouncementDto>("It is already a draft.");

        var now = Now;
        announcement.Status = AnnouncementConstants.StatusDraft;
        announcement.ArchivedAt = null;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> ArchiveAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();
        if (announcement.Status == AnnouncementConstants.StatusArchived) return InvalidState<AdminAnnouncementDto>("It is already archived.");

        var now = Now;
        announcement.Status = AnnouncementConstants.StatusArchived;
        announcement.ArchivedAt = now;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = actor.ActorId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> DuplicateAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var source = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (source is null) return NotFound<AdminAnnouncementDto>();

        var now = Now;
        var title = $"Copy of {source.Title}";
        var copy = new Announcement
        {
            Id = Guid.CreateVersion7(),
            Title = title.Length > AnnouncementConstants.MaxTitleLength ? title[..AnnouncementConstants.MaxTitleLength] : title,
            BodyMarkdown = source.BodyMarkdown,
            Type = source.Type,
            Status = AnnouncementConstants.StatusDraft,
            Placement = source.Placement,
            Variant = source.Variant,
            AccentColor = source.AccentColor,
            Icon = source.Icon,
            ImageUrl = source.ImageUrl,
            Priority = source.Priority,
            Dismissible = source.Dismissible,
            Frequency = source.Frequency,
            AudienceMode = source.AudienceMode,
            AudiencePlanSlugs = [.. source.AudiencePlanSlugs],
            AudienceWorkspaceIds = [.. source.AudienceWorkspaceIds],
            TargetRoles = [.. source.TargetRoles],
            TargetLocales = [.. source.TargetLocales],
            NewUsersWithinDays = source.NewUsersWithinDays,
            CtaLabel = source.CtaLabel,
            CtaUrl = source.CtaUrl,
            SecondaryCtaLabel = source.SecondaryCtaLabel,
            SecondaryCtaUrl = source.SecondaryCtaUrl,
            // The window is not copied: a copy is for running it again, and the original's dates
            // are almost always the part that has to change.
            StartsAt = null,
            EndsAt = null,
            CreatedBy = actor.ActorId,
            CreatedAt = now,
            UpdatedBy = actor.ActorId,
            UpdatedAt = now,
        };

        await _unitOfWork.AnnouncementRepository.AddAsync(copy, ct);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(copy, now));
    }

    public async Task<Result> DeleteAsync(AdminActorContext actor, Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return Result.Failure("Announcement not found.", ErrorCodes.NotFound);
        if (announcement.Status != AnnouncementConstants.StatusDraft)
        {
            return Result.Failure(
                "Only drafts can be deleted. Archive a published announcement instead, so there is a record it ran.",
                ErrorCodes.InvalidState);
        }

        _unitOfWork.AnnouncementRepository.Remove(announcement);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success();
    }

    public async Task<Result<BulkResultDto>> BulkAsync(AdminActorContext actor, AnnouncementBulkRequest request, CancellationToken ct = default)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("publish" or "archive" or "delete" or "duplicate"))
            return Invalid<BulkResultDto>("Action must be publish, archive, delete or duplicate.");
        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Count == 0) return Invalid<BulkResultDto>("Select at least one announcement.");
        if (ids.Count > AnnouncementConstants.MaxBulkItems) return Invalid<BulkResultDto>("Too many announcements in one action.");

        var results = new List<BulkItemResultDto>();
        foreach (var id in ids)
        {
            Result outcome = action switch
            {
                "publish" => await PublishFromBulkAsync(actor, id, ct),
                "archive" => await ArchiveAsync(actor, id, ct),
                "delete" => await DeleteAsync(actor, id, ct),
                _ => await DuplicateAsync(actor, id, ct),
            };
            results.Add(new BulkItemResultDto(id.ToString(), outcome.IsSuccess, outcome.Error));
        }
        return Result.Success(new BulkResultDto(results));
    }

    /// <summary>Bulk publish honours each draft's own start time: a future start schedules it.</summary>
    private async Task<Result> PublishFromBulkAsync(AdminActorContext actor, Guid id, CancellationToken ct)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return Result.Failure("Announcement not found.", ErrorCodes.NotFound);
        var effective = AnnouncementLifecycle.EffectiveStatus(announcement, Now);
        if (effective == AnnouncementConstants.EffectivePublished) return Result.Failure("It is already published.", ErrorCodes.InvalidState);
        var start = announcement.StartsAt is { } s && s > Now ? s : (DateTime?)null;
        return await PublishAsync(actor, id, new PublishAnnouncementRequest(start), ct);
    }

    // ── Assets ───────────────────────────────────────────────────────────────────────────

    public async Task<Result<AnnouncementAssetDto>> UploadAssetAsync(
        AdminActorContext actor, string fileName, string contentType, byte[] content, CancellationToken ct = default)
    {
        var type = (contentType ?? string.Empty).Trim().ToLowerInvariant();
        if (!AnnouncementConstants.AssetContentTypes.Contains(type))
            return Invalid<AnnouncementAssetDto>("Upload a PNG, JPEG, WebP or GIF image.");
        if (content.Length == 0) return Invalid<AnnouncementAssetDto>("The file is empty.");
        if (content.Length > AnnouncementConstants.MaxAssetBytes)
            return Invalid<AnnouncementAssetDto>($"Images must be {AnnouncementConstants.MaxAssetBytes / (1024 * 1024)} MB or smaller.");
        if (!ImageSignature.Matches(type, content))
            return Invalid<AnnouncementAssetDto>("The file is not the image type it claims to be.");

        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "image" : fileName.Trim());
        if (name.Length > 255) name = name[^255..];
        var asset = new AnnouncementAsset
        {
            // Random, not time-ordered: the id is the only thing keeping an unpublished image private.
            Id = Guid.NewGuid(),
            FileName = name,
            ContentType = type,
            SizeBytes = content.Length,
            Content = content,
            CreatedBy = actor.ActorId,
            CreatedAt = Now,
        };
        await _unitOfWork.AnnouncementAssetRepository.AddAsync(asset, ct);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(new AnnouncementAssetDto(asset.Id, AnnouncementRules.AssetPathPrefix + asset.Id, name, type, content.Length));
    }

    public async Task<Result<AnnouncementAssetContent>> GetAssetAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await _unitOfWork.AnnouncementAssetRepository.GetByIdAsync(id, ct);
        return asset is null
            ? Result.Failure<AnnouncementAssetContent>("Image not found.", ErrorCodes.NotFound)
            : Result.Success(new AnnouncementAssetContent(asset.ContentType, asset.Content));
    }

    // ── What the app shows ───────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ViewerAnnouncementDto>>> GetActiveForViewerAsync(
        Guid userId, string? locale = null, string? sessionId = null, CancellationToken ct = default)
    {
        var now = Now;
        var live = await _unitOfWork.AnnouncementRepository.GetLiveAsync(now, ct);
        if (live.Count == 0) return Result.Success<IReadOnlyList<ViewerAnnouncementDto>>([]);

        var viewerLocale = NormalizeLocale(locale);
        var session = NormalizeSession(sessionId);

        ViewerAudience? audience = null;
        if (live.Any(NeedsAudience))
        {
            var resolved = await _viewerAudience.ResolveAsync(userId, live.Any(a => a.NewUsersWithinDays is not null), ct);
            if (resolved.IsSuccess)
            {
                audience = resolved.Value;
            }
            else
            {
                // Fail closed for targeted announcements only: showing a plan-, workspace- or
                // role-scoped notice to someone outside it would leak it, and ALL still reaches everyone.
                _logger.LogWarning("Could not resolve the audience of {UserId}; showing only untargeted announcements. {Error}", userId, resolved.Error);
            }
        }

        var targeted = live.Where(announcement => IsFor(announcement, audience, viewerLocale, now)).ToList();
        var states = await _unitOfWork.AnnouncementViewerStateRepository
            .GetForUserAsync(userId, targeted.Select(announcement => announcement.Id).ToList(), ct);

        IReadOnlyList<ViewerAnnouncementDto> items = targeted
            .Where(announcement => ShouldShow(announcement, states.GetValueOrDefault(announcement.Id), session, now))
            .Take(AnnouncementConstants.MaxActiveForViewer)
            .Select(ToViewerDto)
            .ToList();
        return Result.Success(items);
    }

    public async Task<Result> RecordEventAsync(Guid userId, Guid id, AnnouncementEventRequest request, CancellationToken ct = default)
    {
        var type = Upper(request.Type);
        if (type is null || !AnnouncementConstants.Events.Contains(type))
            return Result.Failure($"Event must be one of {string.Join(", ", AnnouncementConstants.Events)}.", ErrorCodes.ValidationError);

        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null || announcement.Status != AnnouncementConstants.StatusPublished)
            return Result.Failure("Announcement not found.", ErrorCodes.NotFound);
        if (type == AnnouncementConstants.EventDismiss && !announcement.Dismissible)
            return Result.Failure("This announcement cannot be dismissed.", ErrorCodes.InvalidState);

        var session = NormalizeSession(request.SessionId);
        var now = Now;
        var repository = _unitOfWork.AnnouncementViewerStateRepository;
        var state = await repository.GetAsync(id, userId, ct);
        if (state is null)
        {
            state = new AnnouncementViewerState { AnnouncementId = id, UserId = userId };
            await repository.AddAsync(state, ct);
        }

        // One impression per browser session: the banner refetching, or a second tab, is the same
        // look, not a new one.
        var counted = true;
        switch (type)
        {
            case AnnouncementConstants.EventImpression:
                if (session is not null && state.LastSessionId == session && state.ImpressionCount > 0)
                {
                    counted = false;
                    break;
                }
                state.ImpressionCount += 1;
                state.FirstSeenAt ??= now;
                state.LastSeenAt = now;
                state.LastSessionId = session;
                break;
            case AnnouncementConstants.EventDismiss:
                counted = state.DismissedAt is null || state.DismissedSessionId != session;
                state.DismissedAt = now;
                state.DismissedSessionId = session;
                break;
            case AnnouncementConstants.EventCtaClick:
                state.CtaClickCount += 1;
                state.LastClickedAt = now;
                break;
            default:
                state.SecondaryClickCount += 1;
                state.LastClickedAt = now;
                break;
        }

        try
        {
            await _unitOfWork.SaveChangesAsync();
            if (counted)
                await _unitOfWork.AnnouncementDailyStatRepository.IncrementAsync(id, DateOnly.FromDateTime(now), type, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Two tabs recording the first impression at once race on the insert. Analytics are
            // best-effort; the viewer must never see an error for looking at a banner.
            _logger.LogInformation(ex, "Could not record {Event} of announcement {AnnouncementId}.", type, id);
        }
        return Result.Success();
    }

    // ── Rules the viewer read applies ────────────────────────────────────────────────────

    private static bool NeedsAudience(Announcement announcement) =>
        announcement.AudienceMode != AnnouncementConstants.AudienceAll
        || announcement.TargetRoles.Length > 0
        || announcement.NewUsersWithinDays is not null;

    /// <summary>Whether this announcement is meant for someone with this audience and locale.</summary>
    public static bool IsFor(Announcement announcement, ViewerAudience? audience, string? locale, DateTime now)
    {
        var inBase = announcement.AudienceMode switch
        {
            AnnouncementConstants.AudienceAll => true,
            AnnouncementConstants.AudiencePlans => audience is not null
                && announcement.AudiencePlanSlugs.Any(slug => audience.PlanSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase)),
            AnnouncementConstants.AudienceWorkspaces => audience is not null
                && announcement.AudienceWorkspaceIds.Any(id => audience.WorkspaceIds.Contains(id)),
            _ => false,
        };
        if (!inBase) return false;

        if (announcement.TargetRoles.Length > 0
            && (audience is null || !announcement.TargetRoles.Any(role => audience.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))))
            return false;

        // A locale filter with no locale reported is not a match: the client always sends one.
        if (announcement.TargetLocales.Length > 0 && (locale is null || !announcement.TargetLocales.Contains(locale)))
            return false;

        if (announcement.NewUsersWithinDays is { } days
            && (audience?.AccountCreatedAt is not { } created || created < now.AddDays(-days)))
            return false;

        return true;
    }

    /// <summary>Show-frequency and dismissal, against what this person has already seen.</summary>
    public static bool ShouldShow(Announcement announcement, AnnouncementViewerState? state, string? session, DateTime now)
    {
        var sameSession = session is not null && state?.LastSessionId == session;

        if (announcement.Dismissible && state?.DismissedAt is not null)
        {
            // Every-session announcements come back in the next session; everything else stays closed.
            if (announcement.Frequency != AnnouncementConstants.FrequencyEverySession) return false;
            if (session is null || state.DismissedSessionId == session) return false;
        }

        return announcement.Frequency switch
        {
            AnnouncementConstants.FrequencyOnce => state is null || state.ImpressionCount == 0 || sameSession,
            AnnouncementConstants.FrequencyDaily => state?.LastSeenAt is not { } seen || seen <= now.AddDays(-1) || sameSession,
            _ => true,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static void Apply(Announcement announcement, UpsertAnnouncementRequest request, Guid adminId, DateTime now)
    {
        announcement.Title = request.Title;
        announcement.BodyMarkdown = request.BodyMarkdown;
        announcement.Type = request.Type;
        announcement.Placement = request.Placement;
        announcement.Variant = request.Variant;
        announcement.AccentColor = request.AccentColor;
        announcement.Icon = request.Icon;
        announcement.ImageUrl = request.ImageUrl;
        announcement.Priority = request.Priority;
        announcement.Dismissible = request.Dismissible;
        announcement.Frequency = request.Frequency;
        announcement.AudienceMode = request.AudienceMode;
        announcement.AudiencePlanSlugs = [.. request.AudiencePlanSlugs ?? []];
        announcement.AudienceWorkspaceIds = [.. request.AudienceWorkspaceIds ?? []];
        announcement.TargetRoles = [.. request.TargetRoles ?? []];
        announcement.TargetLocales = [.. request.TargetLocales ?? []];
        announcement.NewUsersWithinDays = request.NewUsersWithinDays;
        announcement.CtaLabel = request.CtaLabel;
        announcement.CtaUrl = request.CtaUrl;
        announcement.SecondaryCtaLabel = request.SecondaryCtaLabel;
        announcement.SecondaryCtaUrl = request.SecondaryCtaUrl;
        announcement.StartsAt = request.StartsAt;
        announcement.EndsAt = request.EndsAt;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = adminId;
    }

    private static AdminAnnouncementDto ToAdminDto(Announcement a, DateTime now) =>
        new(
            a.Id, a.Title, a.BodyMarkdown, a.Type, a.Status, AnnouncementLifecycle.EffectiveStatus(a, now),
            a.Placement, a.Variant, a.AccentColor, a.Icon, a.ImageUrl, a.Priority, a.Dismissible, a.Frequency,
            a.AudienceMode, a.AudiencePlanSlugs, a.AudienceWorkspaceIds, a.TargetRoles, a.TargetLocales, a.NewUsersWithinDays,
            a.CtaLabel, a.CtaUrl, a.SecondaryCtaLabel, a.SecondaryCtaUrl,
            a.StartsAt, a.EndsAt, a.PublishedAt, a.ArchivedAt, a.CreatedBy, a.UpdatedBy, a.CreatedAt, a.UpdatedAt);

    private static ViewerAnnouncementDto ToViewerDto(Announcement a) =>
        new(
            a.Id, a.Title, a.BodyMarkdown, a.Type, a.Placement, a.Variant, a.AccentColor, a.Icon, a.ImageUrl,
            a.Priority, a.Dismissible, a.Frequency, a.CtaLabel, a.CtaUrl, a.SecondaryCtaLabel, a.SecondaryCtaUrl,
            a.StartsAt, a.EndsAt, a.PublishedAt);

    private static string? NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var primary = locale.Trim().Replace('_', '-').Split('-')[0].ToLowerInvariant();
        return AnnouncementConstants.Locales.Contains(primary) ? primary : null;
    }

    private static string? NormalizeSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var trimmed = sessionId.Trim();
        return trimmed.Length > AnnouncementConstants.MaxSessionIdLength ? trimmed[..AnnouncementConstants.MaxSessionIdLength] : trimmed;
    }

    private static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static Result<T> NotFound<T>() => Result.Failure<T>("Announcement not found.", ErrorCodes.NotFound);

    private static Result<T> Invalid<T>(string message) => Result.Failure<T>(message, ErrorCodes.ValidationError);

    private static Result<T> InvalidState<T>(string message) => Result.Failure<T>(message, ErrorCodes.InvalidState);
}

/// <summary>The first bytes each allowed image type starts with, so a renamed file is refused.</summary>
public static class ImageSignature
{
    public static bool Matches(string contentType, byte[] content) => contentType switch
    {
        "image/png" => StartsWith(content, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
        "image/jpeg" => StartsWith(content, 0xFF, 0xD8, 0xFF),
        "image/gif" => StartsWith(content, 0x47, 0x49, 0x46, 0x38),
        "image/webp" => content.Length >= 12
            && StartsWith(content, 0x52, 0x49, 0x46, 0x46)
            && content[8] == 0x57 && content[9] == 0x45 && content[10] == 0x42 && content[11] == 0x50,
        _ => false,
    };

    private static bool StartsWith(byte[] content, params byte[] prefix) =>
        content.Length >= prefix.Length && prefix.Select((b, i) => content[i] == b).All(match => match);
}

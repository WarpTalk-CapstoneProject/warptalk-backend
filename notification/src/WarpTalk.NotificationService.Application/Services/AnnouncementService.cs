using Microsoft.Extensions.Logging;
using WarpTalk.NotificationService.Application.DTOs.Announcements;
using WarpTalk.NotificationService.Application.Helpers.Announcements;
using WarpTalk.NotificationService.Application.Interfaces;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.NotificationService.Domain.Rules;
using WarpTalk.Shared;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// The announcements CMS, and the read the app makes to show them.
///
/// Lifecycle: DRAFT → (publish now | schedule) → PUBLISHED → (unpublish → DRAFT | archive →
/// ARCHIVED). Only drafts can be deleted. Scheduled and ended are PUBLISHED read against the clock
/// (<see cref="AnnouncementLifecycle"/>), so a schedule takes effect with no worker involved.
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

    public async Task<Result<AdminAnnouncementPageDto>> ListAsync(AdminAnnouncementListQuery query, CancellationToken ct = default)
    {
        var status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim().ToUpperInvariant();
        if (status is not null && !AnnouncementConstants.EffectiveStatuses.Contains(status))
        {
            return Result.Failure<AdminAnnouncementPageDto>(
                $"Status must be one of {string.Join(", ", AnnouncementConstants.EffectiveStatuses)}.", ErrorCodes.ValidationError);
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var now = Now;

        var repository = _unitOfWork.AnnouncementRepository;
        var (items, total) = await repository.GetPageAsync(new AnnouncementFilter(page, pageSize, status, search), now, ct);
        var counts = await repository.CountByEffectiveStatusAsync(now, search, ct);

        return Result.Success(new AdminAnnouncementPageDto(
            items.Select(item => ToAdminDto(item, now)).ToList(), total, page, pageSize, counts));
    }

    public async Task<Result<AdminAnnouncementDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        return announcement is null ? NotFound<AdminAnnouncementDto>() : Result.Success(ToAdminDto(announcement, Now));
    }

    public async Task<Result<AdminAnnouncementDto>> CreateAsync(Guid adminId, UpsertAnnouncementRequest request, CancellationToken ct = default)
    {
        var normalized = AnnouncementRules.Normalize(request);
        var error = AnnouncementRules.Validate(normalized);
        if (error is not null) return Result.Failure<AdminAnnouncementDto>(error, ErrorCodes.ValidationError);

        var now = Now;
        var announcement = new Announcement
        {
            Id = Guid.CreateVersion7(),
            Status = AnnouncementConstants.StatusDraft,
            CreatedBy = adminId,
            CreatedAt = now,
        };
        Apply(announcement, normalized, adminId, now);

        await _unitOfWork.AnnouncementRepository.AddAsync(announcement, ct);
        await _unitOfWork.SaveChangesAsync();
        _logger.LogInformation("Announcement {AnnouncementId} drafted by {AdminId}.", announcement.Id, adminId);
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> UpdateAsync(Guid adminId, Guid id, UpsertAnnouncementRequest request, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();
        if (announcement.Status == AnnouncementConstants.StatusArchived)
            return InvalidState<AdminAnnouncementDto>("An archived announcement cannot be edited. Move it back to drafts first.");

        var normalized = AnnouncementRules.Normalize(request);
        var error = AnnouncementRules.Validate(normalized);
        if (error is not null) return Result.Failure<AdminAnnouncementDto>(error, ErrorCodes.ValidationError);

        var now = Now;
        Apply(announcement, normalized, adminId, now);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> PublishAsync(Guid adminId, Guid id, PublishAnnouncementRequest request, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();

        var now = Now;
        var startsAt = AnnouncementRules.AsUtc(request.StartsAt);
        if (startsAt is { } scheduled && scheduled <= now)
            return Result.Failure<AdminAnnouncementDto>(
                "A scheduled start must be in the future. To show it now, publish it now instead.", ErrorCodes.ValidationError);

        var effectiveStart = startsAt ?? now;
        if (announcement.EndsAt is { } endsAt && endsAt <= effectiveStart)
            return Result.Failure<AdminAnnouncementDto>(
                "Its end time would have passed before it starts showing. Move the end time or clear it.", ErrorCodes.ValidationError);

        announcement.Status = AnnouncementConstants.StatusPublished;
        // Publish now clears a future start rather than keeping it: "now" has to mean now.
        announcement.StartsAt = startsAt;
        announcement.PublishedAt = now;
        announcement.PublishedBy = adminId;
        announcement.ArchivedAt = null;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = adminId;
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Announcement {AnnouncementId} {Action} by {AdminId}.",
            announcement.Id, startsAt is null ? "published" : $"scheduled for {startsAt:O}", adminId);
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> UnpublishAsync(Guid adminId, Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();
        if (announcement.Status == AnnouncementConstants.StatusDraft)
            return InvalidState<AdminAnnouncementDto>("It is already a draft.");

        var now = Now;
        announcement.Status = AnnouncementConstants.StatusDraft;
        announcement.ArchivedAt = null;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = adminId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> ArchiveAsync(Guid adminId, Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return NotFound<AdminAnnouncementDto>();
        if (announcement.Status == AnnouncementConstants.StatusArchived)
            return InvalidState<AdminAnnouncementDto>("It is already archived.");

        var now = Now;
        announcement.Status = AnnouncementConstants.StatusArchived;
        announcement.ArchivedAt = now;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = adminId;
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(announcement, now));
    }

    public async Task<Result<AdminAnnouncementDto>> DuplicateAsync(Guid adminId, Guid id, CancellationToken ct = default)
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
            AudienceMode = source.AudienceMode,
            AudiencePlanSlugs = [.. source.AudiencePlanSlugs],
            AudienceWorkspaceIds = [.. source.AudienceWorkspaceIds],
            CtaLabel = source.CtaLabel,
            CtaUrl = source.CtaUrl,
            // The window is not copied: a copy is for running it again, and the original's dates
            // are almost always the part that has to change.
            StartsAt = null,
            EndsAt = null,
            CreatedBy = adminId,
            CreatedAt = now,
            UpdatedBy = adminId,
            UpdatedAt = now,
        };

        await _unitOfWork.AnnouncementRepository.AddAsync(copy, ct);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(ToAdminDto(copy, now));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
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

    public async Task<Result<IReadOnlyList<ViewerAnnouncementDto>>> GetActiveForViewerAsync(Guid userId, CancellationToken ct = default)
    {
        var live = await _unitOfWork.AnnouncementRepository.GetLiveAsync(Now, ct);
        if (live.Count == 0) return Result.Success<IReadOnlyList<ViewerAnnouncementDto>>([]);

        ViewerAudience? audience = null;
        if (live.Any(announcement => announcement.AudienceMode != AnnouncementConstants.AudienceAll))
        {
            var resolved = await _viewerAudience.ResolveAsync(userId, ct);
            if (resolved.IsSuccess)
            {
                audience = resolved.Value;
            }
            else
            {
                // Fail closed for targeted announcements only: showing a plan- or workspace-scoped
                // notice to someone outside it would leak it, and ALL still reaches everyone.
                _logger.LogWarning("Could not resolve the audience of {UserId}; showing only announcements for everyone. {Error}", userId, resolved.Error);
            }
        }

        var visible = live.Where(announcement => IsFor(announcement, audience)).ToList();
        var dismissed = await _unitOfWork.AnnouncementDismissalRepository
            .GetDismissedIdsAsync(userId, visible.Select(announcement => announcement.Id).ToList(), ct);

        IReadOnlyList<ViewerAnnouncementDto> items = visible
            .Where(announcement => !dismissed.Contains(announcement.Id))
            .Take(AnnouncementConstants.MaxActiveForViewer)
            .Select(ToViewerDto)
            .ToList();
        return Result.Success(items);
    }

    public async Task<Result> DismissAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var announcement = await _unitOfWork.AnnouncementRepository.GetByIdAsync(id, ct);
        if (announcement is null) return Result.Failure("Announcement not found.", ErrorCodes.NotFound);

        var dismissals = _unitOfWork.AnnouncementDismissalRepository;
        if (!await dismissals.ExistsAsync(id, userId, ct))
        {
            await dismissals.AddAsync(new AnnouncementDismissal { AnnouncementId = id, UserId = userId, DismissedAt = Now }, ct);
            await _unitOfWork.SaveChangesAsync();
        }
        return Result.Success();
    }

    /// <summary>Whether this announcement is meant for someone with this audience.</summary>
    public static bool IsFor(Announcement announcement, ViewerAudience? audience) => announcement.AudienceMode switch
    {
        AnnouncementConstants.AudienceAll => true,
        AnnouncementConstants.AudiencePlans => audience is not null
            && announcement.AudiencePlanSlugs.Any(slug => audience.PlanSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase)),
        AnnouncementConstants.AudienceWorkspaces => audience is not null
            && announcement.AudienceWorkspaceIds.Any(id => audience.WorkspaceIds.Contains(id)),
        _ => false,
    };

    private static void Apply(Announcement announcement, UpsertAnnouncementRequest request, Guid adminId, DateTime now)
    {
        announcement.Title = request.Title;
        announcement.BodyMarkdown = request.BodyMarkdown;
        announcement.Type = request.Type;
        announcement.AudienceMode = request.AudienceMode;
        announcement.AudiencePlanSlugs = [.. request.AudiencePlanSlugs ?? []];
        announcement.AudienceWorkspaceIds = [.. request.AudienceWorkspaceIds ?? []];
        announcement.CtaLabel = request.CtaLabel;
        announcement.CtaUrl = request.CtaUrl;
        announcement.StartsAt = request.StartsAt;
        announcement.EndsAt = request.EndsAt;
        announcement.UpdatedAt = now;
        announcement.UpdatedBy = adminId;
    }

    private static AdminAnnouncementDto ToAdminDto(Announcement a, DateTime now) =>
        new(
            a.Id,
            a.Title,
            a.BodyMarkdown,
            a.Type,
            a.Status,
            AnnouncementLifecycle.EffectiveStatus(a, now),
            a.AudienceMode,
            a.AudiencePlanSlugs,
            a.AudienceWorkspaceIds,
            a.CtaLabel,
            a.CtaUrl,
            a.StartsAt,
            a.EndsAt,
            a.PublishedAt,
            a.ArchivedAt,
            a.CreatedBy,
            a.UpdatedBy,
            a.CreatedAt,
            a.UpdatedAt);

    private static ViewerAnnouncementDto ToViewerDto(Announcement a) =>
        new(a.Id, a.Title, a.BodyMarkdown, a.Type, a.CtaLabel, a.CtaUrl, a.StartsAt, a.EndsAt, a.PublishedAt);

    private static Result<T> NotFound<T>() => Result.Failure<T>("Announcement not found.", ErrorCodes.NotFound);

    private static Result<T> InvalidState<T>(string message) => Result.Failure<T>(message, ErrorCodes.InvalidState);
}

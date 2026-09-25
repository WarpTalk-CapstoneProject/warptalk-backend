using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Domain.Models;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.NotificationService.Application.Services;

/// <summary>
/// The notification service's source for the pending-work inbox (G12). The CMS has no review state, so
/// "waiting on staff" is read from what exists:
///   announcement_scheduled — a scheduled announcement that goes live within 72 hours: someone should
///                            look at it before it does. Leaves when it goes live, or when marked done.
///   announcement_draft     — a draft untouched for 7 days: publish it or archive it.
///   broadcast_failed       — an inbox broadcast whose delivery failed in the last 14 days.
/// </summary>
public interface IContentInboxSourceService
{
    Task<AdminInboxSourceResponse> GetItemsAsync(CancellationToken ct = default);
}

public sealed class ContentInboxSourceService : IContentInboxSourceService
{
    public const int ScheduledWindowHours = 72;
    public const int StaleDraftDays = 7;
    public const int FailedLookbackDays = 14;

    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public ContentInboxSourceService(IUnitOfWork unitOfWork, TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _time = time ?? TimeProvider.System;
    }

    public async Task<AdminInboxSourceResponse> GetItemsAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var take = AdminInbox.MaxItemsPerSource;
        var items = new List<AdminInboxItem>();

        var (scheduled, _) = await _unitOfWork.AnnouncementRepository.GetPageAsync(
            new AnnouncementFilter(1, take, AnnouncementConstants.EffectiveScheduled, Sort: "updated", Descending: false), now, ct);
        foreach (var announcement in scheduled)
        {
            if (announcement.StartsAt is not { } startsRaw) continue;
            var starts = Utc(startsRaw);
            if (starts - now > TimeSpan.FromHours(ScheduledWindowHours)) continue;
            items.Add(new AdminInboxItem(
                $"{AdminInbox.Types.AnnouncementScheduled}:{announcement.Id}:{starts:yyyyMMddHHmm}",
                AdminInbox.Types.AnnouncementScheduled,
                $"Review \"{announcement.Title}\" before it goes live",
                $"Goes live {starts:yyyy-MM-dd HH:mm} UTC · {announcement.Placement}",
                null,
                null,
                Utc(announcement.PublishedAt ?? announcement.UpdatedAt),
                starts,
                starts - now <= TimeSpan.FromHours(24) ? AdminInbox.Priorities.High : AdminInbox.Priorities.Normal,
                $"/admin/announcements/{announcement.Id}",
                NaturalCompletion: false));
        }

        var (drafts, _) = await _unitOfWork.AnnouncementRepository.GetPageAsync(
            new AnnouncementFilter(1, take, AnnouncementConstants.EffectiveDraft, Sort: "updated", Descending: false), now, ct);
        foreach (var draft in drafts.Where(d => now - Utc(d.UpdatedAt) >= TimeSpan.FromDays(StaleDraftDays)))
        {
            var updated = Utc(draft.UpdatedAt);
            items.Add(new AdminInboxItem(
                $"{AdminInbox.Types.AnnouncementDraft}:{draft.Id}",
                AdminInbox.Types.AnnouncementDraft,
                $"Draft \"{draft.Title}\" untouched for {(int)(now - updated).TotalDays} days",
                "Publish it or archive it",
                null,
                null,
                updated,
                updated.AddDays(StaleDraftDays * 2),
                AdminInbox.Priorities.Low,
                $"/admin/announcements/{draft.Id}",
                NaturalCompletion: true));
        }

        var (failed, _) = await _unitOfWork.AdminNotificationRepository.GetPaginatedAsync(
            new AdminNotificationFilter(1, take, Status: NotificationConstants.StatusFailed, CreatedFrom: now.AddDays(-FailedLookbackDays)), ct);
        foreach (var broadcast in failed)
        {
            var created = Utc(broadcast.CreatedAt);
            items.Add(new AdminInboxItem(
                $"{AdminInbox.Types.BroadcastFailed}:{broadcast.Id}",
                AdminInbox.Types.BroadcastFailed,
                $"Broadcast \"{broadcast.Title}\" failed to deliver",
                $"{broadcast.DeliveredCount} delivered before it failed",
                null,
                null,
                created,
                created.AddDays(1),
                AdminInbox.Priorities.High,
                "/admin/announcements",
                // A failed broadcast stays failed; only a person can decide it was handled.
                NaturalCompletion: false));
        }

        var ordered = items.OrderBy(i => i.DueAt).ToList();
        return new AdminInboxSourceResponse(AdminInbox.Sources.Content, now, ordered.Take(take).ToList(), ordered.Count > take);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

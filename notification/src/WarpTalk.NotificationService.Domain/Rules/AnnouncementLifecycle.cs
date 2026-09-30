using System.Linq.Expressions;
using WarpTalk.NotificationService.Domain.Constants;
using WarpTalk.NotificationService.Domain.Entities;

namespace WarpTalk.NotificationService.Domain.Rules;

/// <summary>
/// What an announcement's status means at a given instant.
///
/// Only DRAFT / PUBLISHED / ARCHIVED are stored. SCHEDULED and ENDED are a published announcement
/// whose window has not opened yet or has already closed; deriving them means no worker has to
/// flip a row at the right second for a schedule to take effect, and nothing can leave a row
/// saying "scheduled" after its start time has passed.
///
/// The expressions are the same rule written for the database, so the admin list's filter and
/// counts cannot disagree with the badge on each card.
/// </summary>
public static class AnnouncementLifecycle
{
    public static string EffectiveStatus(Announcement announcement, DateTime now)
    {
        if (announcement.Status == AnnouncementConstants.StatusDraft) return AnnouncementConstants.EffectiveDraft;
        if (announcement.Status == AnnouncementConstants.StatusArchived) return AnnouncementConstants.EffectiveArchived;
        if (announcement.StartsAt is { } startsAt && startsAt > now) return AnnouncementConstants.EffectiveScheduled;
        if (announcement.EndsAt is { } endsAt && endsAt <= now) return AnnouncementConstants.EffectiveEnded;
        return AnnouncementConstants.EffectivePublished;
    }

    /// <summary>Visible to its audience right now.</summary>
    public static bool IsLive(Announcement announcement, DateTime now) =>
        EffectiveStatus(announcement, now) == AnnouncementConstants.EffectivePublished;

    /// <summary>The database form of <see cref="EffectiveStatus"/> == <paramref name="effectiveStatus"/>.</summary>
    public static Expression<Func<Announcement, bool>> HasEffectiveStatus(string effectiveStatus, DateTime now) =>
        effectiveStatus switch
        {
            AnnouncementConstants.EffectiveDraft => a => a.Status == AnnouncementConstants.StatusDraft,
            AnnouncementConstants.EffectiveArchived => a => a.Status == AnnouncementConstants.StatusArchived,
            AnnouncementConstants.EffectiveScheduled => a =>
                a.Status == AnnouncementConstants.StatusPublished && a.StartsAt != null && a.StartsAt > now,
            AnnouncementConstants.EffectiveEnded => a =>
                a.Status == AnnouncementConstants.StatusPublished
                && (a.StartsAt == null || a.StartsAt <= now)
                && a.EndsAt != null && a.EndsAt <= now,
            AnnouncementConstants.EffectivePublished => a =>
                a.Status == AnnouncementConstants.StatusPublished
                && (a.StartsAt == null || a.StartsAt <= now)
                && (a.EndsAt == null || a.EndsAt > now),
            _ => throw new ArgumentOutOfRangeException(nameof(effectiveStatus), effectiveStatus, "Unknown announcement status."),
        };
}

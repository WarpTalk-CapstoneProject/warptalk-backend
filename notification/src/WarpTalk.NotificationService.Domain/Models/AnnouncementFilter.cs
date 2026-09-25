namespace WarpTalk.NotificationService.Domain.Models;

/// <param name="EffectiveStatus">DRAFT, SCHEDULED, PUBLISHED, ENDED, ARCHIVED, or null for all.</param>
/// <param name="Search">Case-insensitive match on the title.</param>
/// <param name="Type">An announcement type, or null for all.</param>
/// <param name="Placement">A placement, or null for all.</param>
/// <param name="Sort">updated (default), created, priority, title or starts.</param>
/// <param name="Descending">Sort direction; titles default to ascending in the UI.</param>
public record AnnouncementFilter(
    int Page = 1,
    int PageSize = 24,
    string? EffectiveStatus = null,
    string? Search = null,
    string? Type = null,
    string? Placement = null,
    string Sort = "updated",
    bool Descending = true);

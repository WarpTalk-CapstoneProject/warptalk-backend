namespace WarpTalk.NotificationService.Domain.Models;

/// <param name="EffectiveStatus">DRAFT, SCHEDULED, PUBLISHED, ENDED, ARCHIVED, or null for all.</param>
/// <param name="Search">Case-insensitive match on the title.</param>
public record AnnouncementFilter(
    int Page = 1,
    int PageSize = 24,
    string? EffectiveStatus = null,
    string? Search = null);

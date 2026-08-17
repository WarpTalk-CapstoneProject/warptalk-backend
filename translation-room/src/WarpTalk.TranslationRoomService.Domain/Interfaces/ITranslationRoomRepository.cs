using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

/// <summary>What the platform-admin meeting directory is being asked for.</summary>
/// <param name="Status">
/// A single room status, or the pseudo-status "live" for anything currently carrying audio.
/// Validated before it reaches here.
/// </param>
public sealed record AdminMeetingFilter(
    string? Status = null,
    Guid? WorkspaceId = null,
    DateTime? From = null,
    DateTime? To = null,
    string Sort = "recent_desc");

/// <summary>
/// One meeting as the platform admin directory lists it — METADATA ONLY.
///
/// There is deliberately no description, no settings blob and no transcript here. An
/// administrator needs to know a meeting happened, for how long, in which languages and how it
/// ended; what was said in it belongs to the workspace that held it. The title is the one piece of
/// customer-authored text kept, because without it a row cannot be identified at all.
/// </summary>
public sealed record AdminMeetingRow(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string TranslationRoomCode,
    string Status,
    string TranslationRoomType,
    string SourceLanguage,
    string TargetLanguages,
    DateTime? ScheduledAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    int? DurationSeconds,
    DateTime CreatedAt);

public interface ITranslationRoomRepository : IGenericRepository<TranslationRoom>
{
    /// <summary>
    /// One page of the platform meeting directory, plus the total the filter matches.
    ///
    /// Soft-deleted rooms are always excluded. Participant counts are NOT joined here — they come
    /// from the participant repository in one grouped call for the page, the same way the
    /// workspace-scoped list already does it.
    /// </summary>
    Task<(IReadOnlyList<AdminMeetingRow> Items, int Total)> GetAdminDirectoryAsync(
        AdminMeetingFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// How many meetings are carrying audio right now, and how many started since
    /// <paramref name="since"/>. Two counts in one call because the header shows them together.
    /// </summary>
    Task<(int Live, int StartedSince)> GetAdminCountsAsync(
        DateTime since,
        CancellationToken ct = default);

    Task<bool> ExistsByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default);
    Task<TranslationRoom?> GetByCodeAsync(string roomCode, IEnumerable<string>? excludedStatuses = null, CancellationToken cancellationToken = default);
    Task<List<TranslationRoom>> GetHistoryByUserIdAsync(Guid userId, int limit, int offset, CancellationToken ct = default);
    Task<int> CountActiveByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}

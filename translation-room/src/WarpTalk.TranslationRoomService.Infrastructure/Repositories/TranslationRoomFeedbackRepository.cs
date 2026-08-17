using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;

namespace WarpTalk.TranslationRoomService.Infrastructure.Repositories;

public class TranslationRoomFeedbackRepository : GenericRepository<TranslationRoomFeedback>, ITranslationRoomFeedbackRepository
{
    /// <summary>
    /// The dimensions in the order the screen reads them: the required one first, then the four
    /// optional ones. Names match the DTO field names so the client never has to translate.
    /// </summary>
    private static readonly (string Name, Expression<Func<TranslationRoomFeedback, int?>> Selector)[] Dimensions =
    [
        ("overallRating", f => f.OverallRating),
        ("translationQuality", f => f.TranslationQuality),
        ("audioQuality", f => f.AudioQuality),
        ("voiceCloneQuality", f => f.VoiceCloneQuality),
        ("aiSummaryQuality", f => f.AiSummaryQuality),
    ];

    private const string EndedStatus = nameof(RoomStatus.ENDED);

    public TranslationRoomFeedbackRepository(TranslationRoomDbContext context) : base(context)
    {
    }

    public async Task<(AdminFeedbackTotals Totals, IReadOnlyList<AdminFeedbackDimensionStats> Dimensions)>
        GetAdminStatsAsync(AdminFeedbackFilter filter, CancellationToken ct = default)
    {
        var scoped = Scoped(filter);

        var responseCount = await scoped.CountAsync(ct);
        var roomsWithFeedback = await scoped
            .Select(f => f.TranslationRoomId)
            .Distinct()
            .CountAsync(ct);

        // The denominator. Counted over rooms that ENDED in the window, because a scheduled or
        // cancelled meeting was never in a position to be rated and including it would make the
        // response rate look worse than it is.
        var endedRooms = await _context.TranslationRooms
            .Where(r => r.DeletedAt == null
                        && r.Status == EndedStatus
                        && r.EndedAt != null
                        && r.EndedAt >= filter.From
                        && r.EndedAt < filter.To
                        && (filter.WorkspaceId == null || r.WorkspaceId == filter.WorkspaceId))
            .CountAsync(ct);

        var dimensions = new List<AdminFeedbackDimensionStats>(Dimensions.Length);
        foreach (var (name, selector) in Dimensions)
        {
            dimensions.Add(await DimensionAsync(scoped, name, selector, ct));
        }

        return (
            new AdminFeedbackTotals(responseCount, roomsWithFeedback, endedRooms),
            dimensions);
    }

    public async Task<(IReadOnlyList<AdminFeedbackCommentRow> Items, int Total)> GetAdminCommentsAsync(
        AdminFeedbackFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        // A comment of whitespace is not a comment, and `!= ""` does not catch one — the write
        // path trims before storing, but rows predating that are already in the table. Trim()
        // translates to btrim() on Npgsql, so the test runs in the database.
        var query = Scoped(filter)
            .Where(f => f.Comments != null && f.Comments.Trim() != "");

        var total = await query.CountAsync(ct);

        // Ordered on the ENTITY, before the projection. Ordering a positional record by one of
        // its own properties does not translate — EF cannot map a constructor parameter back to
        // the expression it came from, and the endpoint 500s on every call it ever serves.
        var rows = await query
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new
            {
                f.TranslationRoomId,
                f.TranslationRoom.WorkspaceId,
                RoomTitle = f.TranslationRoom.Title,
                f.OverallRating,
                Comment = f.Comments!,
                f.CreatedAt,
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new AdminFeedbackCommentRow(
                r.TranslationRoomId,
                r.WorkspaceId,
                r.RoomTitle,
                r.OverallRating,
                r.Comment,
                r.CreatedAt))
            .ToList();

        return (items, total);
    }

    private IQueryable<TranslationRoomFeedback> Scoped(AdminFeedbackFilter filter) =>
        _dbSet
            .Where(f => f.CreatedAt >= filter.From && f.CreatedAt < filter.To)
            .Where(f => filter.WorkspaceId == null
                        || f.TranslationRoom.WorkspaceId == filter.WorkspaceId)
            // A rating on a soft-deleted room is still a rating of the product, but the room it
            // names cannot be opened, so it is left out of both the statistics and the comments
            // rather than appearing as a row that goes nowhere.
            .Where(f => f.TranslationRoom.DeletedAt == null);

    private static async Task<AdminFeedbackDimensionStats> DimensionAsync(
        IQueryable<TranslationRoomFeedback> scoped,
        string name,
        Expression<Func<TranslationRoomFeedback, int?>> selector,
        CancellationToken ct)
    {
        // GROUP BY rating, which gives the distribution and the count in one pass; the mean is
        // then arithmetic over at most five rows rather than a second aggregate query.
        var buckets = await scoped
            .Select(selector)
            .Where(rating => rating != null)
            .GroupBy(rating => rating!.Value)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var distribution = new int[5];
        var responseCount = 0;
        var weighted = 0L;

        foreach (var bucket in buckets)
        {
            // Defensive: the column is CHECK-constrained to 1..5, but an average that silently
            // includes an out-of-range value would be wrong in a way nobody could see.
            if (bucket.Rating is < 1 or > 5) continue;
            distribution[bucket.Rating - 1] = bucket.Count;
            responseCount += bucket.Count;
            weighted += (long)bucket.Rating * bucket.Count;
        }

        return new AdminFeedbackDimensionStats(
            name,
            responseCount,
            // Null, not 0. Nobody rating a dimension is not the same as everybody rating it
            // badly, and 0.0 out of 5 is the worst score there is.
            responseCount == 0 ? null : (double)weighted / responseCount,
            distribution);
    }
}

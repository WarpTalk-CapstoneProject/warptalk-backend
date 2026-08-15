using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Domain.Entities;

namespace WarpTalk.TranslationRoomService.Domain.Interfaces;

/// <summary>
/// The window a feedback report covers. Measured against when the rating was SUBMITTED, not when
/// the meeting ran — someone rating a meeting from last week is telling you about this week.
/// </summary>
public sealed record AdminFeedbackFilter(DateTime From, DateTime To, Guid? WorkspaceId = null);

/// <summary>
/// One rating dimension over the window.
/// </summary>
/// <param name="ResponseCount">
/// How many people answered THIS dimension. Four of the five are optional, so a dimension's
/// average is over its own respondents and not over everyone — an average of 4.8 from three
/// people does not belong beside one from three hundred without saying so.
/// </param>
/// <param name="Distribution">
/// Counts for ratings 1..5, index 0 being a rating of 1. A mean of 3.0 built from all-threes and
/// one built from half ones and half fives are the same number and completely different feedback.
/// </param>
public sealed record AdminFeedbackDimensionStats(
    string Dimension,
    int ResponseCount,
    double? AverageRating,
    IReadOnlyList<int> Distribution);

/// <summary>
/// The denominators. Without <paramref name="EndedRooms"/> an average rating hides its response
/// rate, and "4.6 out of 5" reads the same whether 90% or 2% of meetings were ever rated.
/// </summary>
public sealed record AdminFeedbackTotals(int ResponseCount, int RoomsWithFeedback, int EndedRooms);

/// <summary>
/// One free-text comment.
///
/// No user id, deliberately. A rating is feedback about the product; attaching a person to it
/// turns a quality signal into a record about that person, and nothing on the admin screen this
/// feeds acts on a person.
/// </summary>
public sealed record AdminFeedbackCommentRow(
    Guid TranslationRoomId,
    Guid WorkspaceId,
    string RoomTitle,
    int OverallRating,
    string Comment,
    DateTime CreatedAt);

public interface ITranslationRoomFeedbackRepository : IGenericRepository<TranslationRoomFeedback>
{
    /// <summary>Totals and per-dimension statistics for the window.</summary>
    Task<(AdminFeedbackTotals Totals, IReadOnlyList<AdminFeedbackDimensionStats> Dimensions)>
        GetAdminStatsAsync(AdminFeedbackFilter filter, CancellationToken ct = default);

    /// <summary>
    /// One page of comments, newest first, plus the total that matched. Ratings with no comment
    /// are excluded — they are already counted in the statistics.
    /// </summary>
    Task<(IReadOnlyList<AdminFeedbackCommentRow> Items, int Total)> GetAdminCommentsAsync(
        AdminFeedbackFilter filter,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

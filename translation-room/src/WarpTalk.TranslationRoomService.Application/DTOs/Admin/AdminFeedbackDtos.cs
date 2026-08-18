using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.TranslationRoomService.Application.DTOs.Admin;

/// <summary>Query string contract for the platform feedback report. Bound with [FromQuery].</summary>
public record AdminFeedbackQuery : AdminPageRequest
{
    public Guid? WorkspaceId { get; init; }

    /// <summary>
    /// Measured against when the rating was SUBMITTED, not when the meeting ran. Someone rating
    /// last week's meeting today is telling you about today.
    /// </summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

/// <summary>
/// One rating dimension over the window.
/// </summary>
/// <param name="ResponseCount">
/// How many people answered THIS dimension. Four of the five are optional, so an average of 4.8
/// from three people must not sit beside one from three hundred without saying which is which.
/// </param>
/// <param name="AverageRating">
/// Null when nobody rated it. Not zero — zero out of five is the worst score there is, and
/// "nobody answered" is not a bad score.
/// </param>
/// <param name="Distribution">
/// Counts for ratings 1..5, index 0 being a rating of 1. A mean of 3.0 from all threes and one
/// from half ones and half fives are the same number and entirely different feedback.
/// </param>
public record AdminFeedbackDimensionDto(
    string Dimension,
    int ResponseCount,
    double? AverageRating,
    IReadOnlyList<int> Distribution);

/// <summary>
/// The report.
/// </summary>
/// <param name="EndedMeetings">
/// Meetings that ended in the window — the denominator. Without it, "4.6 out of 5" reads the same
/// whether 90% of meetings were rated or 2% were.
/// </param>
/// <param name="ResponseRate">
/// Rooms that received at least one rating, over meetings that ended. Null when nothing ended in
/// the window, because a rate with no denominator is not zero.
/// </param>
public record AdminFeedbackSummaryDto(
    DateTime From,
    DateTime To,
    int ResponseCount,
    int RatedMeetings,
    int EndedMeetings,
    double? ResponseRate,
    IReadOnlyList<AdminFeedbackDimensionDto> Dimensions);

/// <summary>
/// One free-text comment.
///
/// No user id, deliberately. A rating is feedback about the product; attaching a person to it
/// turns a quality signal into a record about that person, and nothing on this screen acts on a
/// person.
/// </summary>
public record AdminFeedbackCommentDto(
    Guid TranslationRoomId,
    Guid WorkspaceId,
    string RoomTitle,
    int OverallRating,
    string Comment,
    DateTime CreatedAt);

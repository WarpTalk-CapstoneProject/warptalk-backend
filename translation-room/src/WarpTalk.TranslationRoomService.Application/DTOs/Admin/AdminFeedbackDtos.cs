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

    /// <summary>Comments only: <c>recent</c> (default, newest first) or <c>lowest</c> (lowest overall rating first).</summary>
    public string? Sort { get; init; }
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
/// <param name="ResponseShare">
/// WT-694: respondents who answered this dimension over all respondents in the window. Null when
/// nobody responded at all.
/// </param>
/// <param name="Confidence">
/// <c>none</c> (nobody answered — show "no data", not a score), <c>low</c> (thin sample; see
/// <paramref name="ConfidenceNote"/>) or <c>ok</c>. Thresholds live on AdminFeedbackInsightsCalculator.
/// </param>
/// <param name="PreviousAverageRating">The same dimension over the previous window of equal length; null when unanswered.</param>
/// <param name="PreviousConfidence">How far the previous figure can be trusted, same rules.</param>
/// <param name="AverageDelta">Current minus previous average; null unless both exist.</param>
public record AdminFeedbackDimensionDto(
    string Dimension,
    int ResponseCount,
    double? AverageRating,
    IReadOnlyList<int> Distribution,
    double? ResponseShare = null,
    string Confidence = "none",
    string? ConfidenceNote = null,
    int PreviousResponseCount = 0,
    double? PreviousAverageRating = null,
    string PreviousConfidence = "none",
    double? AverageDelta = null);

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
/// <param name="PreviousFrom">WT-694: start of the comparison window — the same length, immediately before.</param>
/// <param name="Confidence">Survey-level <c>none</c> / <c>low</c> / <c>ok</c>; <paramref name="ConfidenceNote"/> says why.</param>
/// <param name="LowestDimension">
/// The weakest dimension by average, preferring ones with a trustworthy sample; null when nothing
/// was rated. <paramref name="LowestDimensionNote"/> is set when every candidate was thin.
/// </param>
/// <param name="DimensionsWithoutData">Dimensions nobody answered in the window, e.g. voice clone quality.</param>
/// <param name="MinResponses">The low-confidence threshold on answers, so the page can state it.</param>
/// <param name="MinRate">The low-confidence threshold on response rate / share (0..1).</param>
public record AdminFeedbackSummaryDto(
    DateTime From,
    DateTime To,
    int ResponseCount,
    int RatedMeetings,
    int EndedMeetings,
    double? ResponseRate,
    IReadOnlyList<AdminFeedbackDimensionDto> Dimensions,
    DateTime? PreviousFrom = null,
    DateTime? PreviousTo = null,
    int PreviousResponseCount = 0,
    double? PreviousResponseRate = null,
    string Confidence = "none",
    string? ConfidenceNote = null,
    string? LowestDimension = null,
    string? LowestDimensionNote = null,
    IReadOnlyList<string>? DimensionsWithoutData = null,
    int MinResponses = 10,
    double MinRate = 0.10);

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

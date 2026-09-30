using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Application.Helpers;

/// <summary>
/// WT-694: turns the feedback statistics into something an administrator can act on — how far to
/// trust each figure, which dimension is weakest, and how it moved — without inventing a number.
/// Pure: no clock, no I/O.
///
/// Every judgement here is a threshold the page states rather than hides:
/// <list type="bullet">
/// <item><b>none</b> — nobody answered this dimension. Its average is null, not 0.</item>
/// <item><b>low</b> — fewer than <see cref="MinResponses"/> answers, or fewer than
/// <see cref="MinRate"/> of the eligible meetings rated at all, or fewer than <see cref="MinRate"/>
/// of the respondents answered this (optional) dimension.</item>
/// <item><b>ok</b> — none of the above.</item>
/// </list>
/// </summary>
public static class AdminFeedbackInsightsCalculator
{
    public const int MinResponses = 10;
    public const double MinRate = 0.10;

    public const string None = "none";
    public const string Low = "low";
    public const string Ok = "ok";

    public sealed record DimensionInsight(
        string Dimension,
        double? ResponseShare,
        string Confidence,
        string? ConfidenceNote,
        int PreviousResponseCount,
        double? PreviousAverageRating,
        string PreviousConfidence,
        double? AverageDelta);

    /// <summary>Survey-level confidence: the same rules as a dimension, over every response.</summary>
    public static (string Confidence, string? Note) Survey(int responseCount, double? responseRate)
    {
        if (responseCount == 0) return (None, "No feedback was submitted in this period.");
        if (responseCount < MinResponses)
            return (Low, $"Only {responseCount} response{Plural(responseCount)} — fewer than {MinResponses}.");
        if (responseRate is { } rate && rate < MinRate)
            return (Low, $"Only {Percent(rate)} of ended meetings were rated — below {Percent(MinRate)}.");
        return (Ok, null);
    }

    /// <summary>Rated meetings over ended meetings. Null when nothing ended: no denominator, no rate.</summary>
    public static double? ResponseRate(AdminFeedbackTotals totals)
        => totals.EndedRooms == 0 ? null : (double)totals.RoomsWithFeedback / totals.EndedRooms;

    public static IReadOnlyList<DimensionInsight> Dimensions(
        AdminFeedbackTotals current,
        IReadOnlyList<AdminFeedbackDimensionStats> currentDimensions,
        AdminFeedbackTotals previous,
        IReadOnlyList<AdminFeedbackDimensionStats> previousDimensions)
    {
        var currentRate = ResponseRate(current);
        var previousRate = ResponseRate(previous);

        return currentDimensions
            .Select(dimension =>
            {
                var before = previousDimensions.FirstOrDefault(d => d.Dimension == dimension.Dimension);
                var (confidence, note) = Confidence(dimension, current.ResponseCount, currentRate);
                var (previousConfidence, _) = before is null
                    ? (None, (string?)null)
                    : Confidence(before, previous.ResponseCount, previousRate);

                return new DimensionInsight(
                    dimension.Dimension,
                    current.ResponseCount == 0 ? null : (double)dimension.ResponseCount / current.ResponseCount,
                    confidence,
                    note,
                    before?.ResponseCount ?? 0,
                    before?.AverageRating,
                    previousConfidence,
                    dimension.AverageRating is { } now && before?.AverageRating is { } then
                        ? Math.Round(now - then, 2)
                        : null);
            })
            .ToList();
    }

    /// <summary>
    /// The dimension to look at first: the lowest average among those anyone answered, preferring
    /// dimensions whose sample can be trusted. Null when nothing was rated.
    /// </summary>
    public static (string? Dimension, string? Note) Lowest(
        IReadOnlyList<AdminFeedbackDimensionStats> dimensions,
        IReadOnlyList<DimensionInsight> insights)
    {
        var rated = dimensions
            .Where(d => d.AverageRating is not null && d.ResponseCount > 0)
            .Select(d => (Stats: d, Insight: insights.First(i => i.Dimension == d.Dimension)))
            .ToList();
        if (rated.Count == 0) return (null, null);

        var trusted = rated.Where(r => r.Insight.Confidence == Ok).ToList();
        var pool = trusted.Count > 0 ? trusted : rated;
        // Stable on ties: the list order is the screen order, required dimension first.
        var lowest = pool.OrderBy(r => r.Stats.AverageRating!.Value).First();

        return (
            lowest.Stats.Dimension,
            trusted.Count > 0
                ? null
                : "No dimension has a sample large enough to trust; this is the lowest of the thin ones.");
    }

    private static (string Confidence, string? Note) Confidence(
        AdminFeedbackDimensionStats dimension, int surveyResponses, double? surveyRate)
    {
        var n = dimension.ResponseCount;
        if (n == 0) return (None, "No responses for this dimension in this period.");
        if (n < MinResponses)
            return (Low, $"Only {n} response{Plural(n)} — fewer than {MinResponses}.");
        if (surveyRate is { } rate && rate < MinRate)
            return (Low, $"Only {Percent(rate)} of ended meetings were rated — below {Percent(MinRate)}.");
        if (surveyResponses > 0 && (double)n / surveyResponses < MinRate)
            return (Low, $"Answered by only {Percent((double)n / surveyResponses)} of respondents.");
        return (Ok, null);
    }

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    private static string Percent(double rate) =>
        (rate * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

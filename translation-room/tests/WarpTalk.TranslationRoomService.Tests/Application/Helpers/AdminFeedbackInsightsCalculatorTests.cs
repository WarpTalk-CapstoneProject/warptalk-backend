using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using WarpTalk.TranslationRoomService.Application.Helpers;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;
using static WarpTalk.TranslationRoomService.Application.Helpers.AdminFeedbackInsightsCalculator;

namespace WarpTalk.TranslationRoomService.Tests.Application.Helpers;

/// <summary>
/// WT-694: what the feedback page may claim. A thin sample is flagged rather than presented as a
/// verdict, an unanswered dimension is "no data" rather than a score, and a trend exists only when
/// both periods have a figure.
/// </summary>
public class AdminFeedbackInsightsCalculatorTests
{
    private static AdminFeedbackDimensionStats Stats(string name, int n, double? average) =>
        new(name, n, average, new[] { 0, 0, 0, 0, n });

    private static readonly AdminFeedbackTotals Healthy = new(ResponseCount: 40, RoomsWithFeedback: 30, EndedRooms: 60);

    [Fact]
    public void A_dimension_nobody_answered_is_none_with_no_share_of_a_score()
    {
        var current = new[] { Stats("overallRating", 40, 4.2), Stats("voiceCloneQuality", 0, null) };

        var insights = Dimensions(Healthy, current, Healthy, current);

        var clone = insights.Single(i => i.Dimension == "voiceCloneQuality");
        clone.Confidence.Should().Be(None);
        clone.ConfidenceNote.Should().Contain("No responses");
        clone.AverageDelta.Should().BeNull();
        clone.ResponseShare.Should().Be(0);
    }

    [Fact]
    public void Fewer_than_ten_answers_is_low_confidence()
    {
        var current = new[] { Stats("overallRating", 40, 4.2), Stats("audioQuality", 9, 3.1) };

        var audio = Dimensions(Healthy, current, Healthy, current).Single(i => i.Dimension == "audioQuality");

        audio.Confidence.Should().Be(Low);
        audio.ConfidenceNote.Should().Contain("Only 9 responses");
    }

    [Fact]
    public void A_response_rate_under_ten_percent_makes_every_dimension_low()
    {
        // 40 answers, but from 5 of 100 ended meetings: plenty of rows, not a representative sample.
        var thin = new AdminFeedbackTotals(ResponseCount: 40, RoomsWithFeedback: 5, EndedRooms: 100);
        var current = new[] { Stats("overallRating", 40, 4.8) };

        var overall = Dimensions(thin, current, thin, current).Single();

        overall.Confidence.Should().Be(Low);
        overall.ConfidenceNote.Should().Contain("5%");
        Survey(40, ResponseRate(thin)).Confidence.Should().Be(Low);
    }

    [Fact]
    public void An_optional_dimension_answered_by_under_ten_percent_of_respondents_is_low()
    {
        var totals = new AdminFeedbackTotals(ResponseCount: 200, RoomsWithFeedback: 100, EndedRooms: 120);
        var current = new[] { Stats("overallRating", 200, 4.0), Stats("aiSummaryQuality", 12, 2.0) };

        var summary = Dimensions(totals, current, totals, current).Single(i => i.Dimension == "aiSummaryQuality");

        summary.Confidence.Should().Be(Low);
        summary.ResponseShare.Should().BeApproximately(0.06, 0.0001);
    }

    [Fact]
    public void The_trend_is_current_minus_previous_and_null_when_either_side_is_empty()
    {
        var current = new[] { Stats("overallRating", 40, 4.25), Stats("translationQuality", 20, 3.5) };
        var previous = new[] { Stats("overallRating", 30, 4.0), Stats("translationQuality", 0, null) };

        var insights = Dimensions(Healthy, current, Healthy, previous);

        var overall = insights.Single(i => i.Dimension == "overallRating");
        overall.AverageDelta.Should().Be(0.25);
        overall.PreviousAverageRating.Should().Be(4.0);
        overall.PreviousResponseCount.Should().Be(30);
        overall.PreviousConfidence.Should().Be(Ok);

        var translation = insights.Single(i => i.Dimension == "translationQuality");
        translation.AverageDelta.Should().BeNull();
        translation.PreviousConfidence.Should().Be(None);
    }

    [Fact]
    public void The_lowest_dimension_prefers_a_trustworthy_sample_over_a_thin_one()
    {
        // audio has the lowest average, but from 3 answers; translation is the lowest trusted one.
        var current = new List<AdminFeedbackDimensionStats>
        {
            Stats("overallRating", 40, 4.4),
            Stats("translationQuality", 30, 3.6),
            Stats("audioQuality", 3, 1.0),
            Stats("voiceCloneQuality", 0, null),
        };
        var insights = Dimensions(Healthy, current, Healthy, current);

        var (lowest, note) = Lowest(current, insights);

        lowest.Should().Be("translationQuality");
        note.Should().BeNull();
    }

    [Fact]
    public void When_every_sample_is_thin_the_lowest_is_still_named_but_flagged()
    {
        var totals = new AdminFeedbackTotals(ResponseCount: 4, RoomsWithFeedback: 4, EndedRooms: 10);
        var current = new List<AdminFeedbackDimensionStats>
        {
            Stats("overallRating", 4, 4.0),
            Stats("audioQuality", 2, 2.5),
        };
        var insights = Dimensions(totals, current, totals, current);

        var (lowest, note) = Lowest(current, insights);

        lowest.Should().Be("audioQuality");
        note.Should().Contain("No dimension has a sample large enough");
    }

    [Fact]
    public void Nothing_rated_means_no_lowest_dimension_and_a_none_survey()
    {
        var empty = new AdminFeedbackTotals(0, 0, 12);
        var current = new List<AdminFeedbackDimensionStats> { Stats("overallRating", 0, null) };

        var (lowest, _) = Lowest(current, Dimensions(empty, current, empty, current));

        lowest.Should().BeNull();
        Survey(0, ResponseRate(empty)).Confidence.Should().Be(None);
    }

    [Fact]
    public void Nothing_ended_means_no_response_rate_rather_than_zero()
    {
        ResponseRate(new AdminFeedbackTotals(3, 3, 0)).Should().BeNull();
    }
}

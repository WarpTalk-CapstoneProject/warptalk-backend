using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>The pure half of measured dubbing cost: day selection, STT exclusion, and today's partial day.</summary>
public sealed class CartesiaDubbingCostTests
{
    private static DateTime Utc(int d, int h = 0, int min = 0) => new(2026, 9, d, h, min, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("stt", null, true)]
    [InlineData("speech_to_text", null, true)]
    [InlineData("ink-whisper", null, true)]
    [InlineData("cap_1", "Speech-to-Text", true)]
    [InlineData("tts", "Text to speech", false)]
    [InlineData("infill", null, false)]
    [InlineData("voice_clone", null, false)]
    public void SpeechToText_IsTheOnlyCapabilityNotChargedToDubbing(string id, string? label, bool isStt)
        => ProviderUsageConstants.IsSpeechToTextCapability(id, label).Should().Be(isStt);

    [Fact]
    public void UtcDays_StopAtNow()
    {
        CartesiaDubbingCost.UtcDaysOf(Utc(1), Utc(30), now: Utc(3, 5))
            .Should().Equal(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 3));
        CartesiaDubbingCost.UtcDaysOf(Utc(5), Utc(6), now: Utc(4)).Should().BeEmpty();
    }

    [Fact]
    public void Today_IsMeasuredAsOfItsLastSync_AndUnsyncedDaysKeepTheirRateCardCost()
    {
        var days = new[]
        {
            // Today, read at 08:00: the whole 8 hours so far are inside the period.
            new CartesiaUsageDay(new DateOnly(2026, 9, 3), 1_000, 0, Utc(3, 8)),
        };
        var dubbing = new[]
        {
            new DailyChargeTypeConsumption(new DateOnly(2026, 9, 2), "AUDIO_DUBBING_STANDARD", 30, 1, 30, 1, 0.0588m),
            new DailyChargeTypeConsumption(new DateOnly(2026, 9, 3), "AUDIO_DUBBING_STANDARD", 10, 2, 0, 0, 0m),
        };

        var measured = CartesiaDubbingCost.Measure(Utc(2), Utc(4), now: Utc(3, 8, 5), days, dubbing, 0.0000392m);

        measured.Basis.Should().Be("mixed");
        measured.MeasuredDays.Should().Be(1);
        measured.EstimatedDays.Should().Be(1);
        measured.ProRatedDays.Should().Be(0);
        measured.MeasuredCredits.Should().Be(1_000m);
        measured.MeasuredUsd.Should().Be(0.0392m);
        // Today's uncovered rows become covered; yesterday's estimate is untouched.
        measured.MeasuredDayCredits.Should().Be(10);
        measured.ReplacedRateCardUsd.Should().Be(0m);
        measured.EstimatedDubbingCredits.Should().Be(30);

        var totals = new ConsumptionTotals(40, 3, 0, 30, 1, 0.0588m,
            new[] { new ChargeTypeCoverage("AUDIO_DUBBING_STANDARD", 40, 30) });
        var applied = CartesiaDubbingCost.Apply(totals, measured);
        applied.CostCoveredCredits.Should().Be(40);
        applied.CostCoveredTransactions.Should().Be(3);
        applied.ProviderCostUsd.Should().Be(0.098m);
        applied.ByChargeType!.Single().UncoveredCredits.Should().Be(0);
    }

    [Fact]
    public void MeasuredUsage_WithNoWarpTalkCharge_IsStillACost()
    {
        var measured = CartesiaDubbingCost.Measure(
            Utc(1), Utc(2), now: Utc(5),
            new[] { new CartesiaUsageDay(new DateOnly(2026, 9, 1), 500, 0, Utc(2, 1)) },
            Array.Empty<DailyChargeTypeConsumption>(),
            0.0000392m);

        var cost = AdminBillingInsightsCalculator.AiProviderCost(
            new ConsumptionTotals(0, 0, 0, 0, 0, 0m), 25_000m, measured, filteredToApiKey: true);

        cost.Value.Should().Be(490m, "500 × 0.0000392 × 25,000");
        cost.Note.Should().Be("dubbing measured from Cartesia usage: 500 credits × $0.0000392 over 1 UTC day; USD converted at 25,000 VND/USD");
    }
}

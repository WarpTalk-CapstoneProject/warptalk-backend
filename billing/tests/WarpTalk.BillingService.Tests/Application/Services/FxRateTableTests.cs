using FluentAssertions;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// The USD→VND rate of a day: recorded history first (manual &gt; Stripe FX quote &gt; Stripe charge), the
/// nearest recorded day otherwise — and the old single configured number only when nothing was recorded.
/// </summary>
public sealed class FxRateTableTests
{
    private static readonly DateOnly Sep20 = new(2026, 9, 20);

    private static FxRate Row(DateOnly day, decimal rate, string source, int hour = 1) => new()
    {
        BaseCurrency = "USD",
        QuoteCurrency = "VND",
        RateDate = day,
        Rate = rate,
        Source = source,
        FetchedAt = day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc),
    };

    [Fact]
    public void A_day_uses_its_own_rate_and_the_most_authoritative_source_wins()
    {
        var table = FxRateTable.From(
            [
                Row(Sep20, 26_009m, FxRateConstants.Sources.StripeCharge),
                Row(Sep20, 25_990m, FxRateConstants.Sources.StripeFxQuote),
                Row(Sep20.AddDays(1), 25_500m, FxRateConstants.Sources.StripeFxQuote),
                Row(Sep20.AddDays(1), 27_000m, FxRateConstants.Sources.Manual),
            ],
            configured: 26_300m);

        table.Resolve(Sep20).Should().Match<FxRateResolution>(r =>
            r.Rate == 25_990m && r.Source == FxRateConstants.Sources.StripeFxQuote && r.Basis == FxRateBasis.Exact);
        table.Resolve(Sep20.AddDays(1)).Rate.Should().Be(27_000m, "an override beats Stripe on the day it covers");
    }

    [Fact]
    public void Past_periods_keep_the_rate_of_their_day_not_todays()
    {
        var table = FxRateTable.From(
            [Row(Sep20, 25_000m, FxRateConstants.Sources.StripeCharge), Row(Sep20.AddDays(10), 26_000m, FxRateConstants.Sources.StripeFxQuote)],
            configured: 26_300m);

        table.Resolve(Sep20).Rate.Should().Be(25_000m);
        table.Resolve(Sep20.AddDays(10)).Rate.Should().Be(26_000m);
    }

    [Fact]
    public void A_missing_day_borrows_the_last_recorded_one_and_says_so()
    {
        var table = FxRateTable.From([Row(Sep20, 25_990m, FxRateConstants.Sources.StripeFxQuote)], configured: 26_300m);

        var later = table.Resolve(Sep20.AddDays(3));
        later.Rate.Should().Be(25_990m);
        later.Basis.Should().Be(FxRateBasis.CarriedForward);

        var earlier = table.Resolve(Sep20.AddDays(-30));
        earlier.Rate.Should().Be(25_990m);
        earlier.Basis.Should().Be(FxRateBasis.BeforeFirstRecord);

        FxRateTable.Describe([later]).Should().Contain("1 day had no rate of its own");
    }

    [Fact]
    public void With_nothing_recorded_the_configured_rate_is_the_fallback_and_nothing_is_invented()
    {
        FxRateTable.From([], configured: 26_300m).Resolve(Sep20).Should().Match<FxRateResolution>(r =>
            r.Rate == 26_300m && r.Basis == FxRateBasis.Configured);
        FxRateTable.From([], configured: null).Resolve(Sep20).Rate.Should().BeNull();
    }

    [Fact]
    public void Describe_names_one_rate_or_the_range()
    {
        var table = FxRateTable.From(
            [Row(Sep20, 25_990m, FxRateConstants.Sources.StripeFxQuote), Row(Sep20.AddDays(1), 26_010m, FxRateConstants.Sources.StripeCharge)],
            configured: null);

        FxRateTable.Describe([table.Resolve(Sep20)]).Should()
            .Be("USD converted at 25,990 VND/USD (Stripe FX quote, 2026-09-20)");
        FxRateTable.Describe([table.Resolve(Sep20), table.Resolve(Sep20.AddDays(1))]).Should()
            .Be("USD converted at each day's rate (25,990–26,010 VND/USD; Stripe FX quote, Stripe charge conversion)");
        FxRateTable.Describe([]).Should().BeNull();
    }
}

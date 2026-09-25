using FluentAssertions;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using static WarpTalk.BillingService.Application.Services.ProviderMetricsCalculator;

namespace WarpTalk.BillingService.Tests.Application.Providers;

/// <summary>The definitions of the admin Providers page (ProviderMetricsCalculator), on plain values.</summary>
public sealed class ProviderMetricsCalculatorTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 30, 0, DateTimeKind.Utc);
    private static readonly FxRateTable Fx = FxRateTable.Flat(25_000m);

    private static ProviderInputs Inputs(
        string provider,
        IReadOnlyList<ConsumptionSlotRow>? slots = null,
        IReadOnlyList<CartesiaUsageDay>? cartesia = null,
        IReadOnlyList<ProviderCallStat>? calls = null,
        DateTime? trackedSince = null,
        IReadOnlyList<MediaUsageHourRow>? media = null,
        decimal? livekitPrice = null,
        IReadOnlyList<ProviderPaymentRow>? payments = null,
        FxRateTable? fx = null)
        => new(provider, Now, fx ?? Fx, slots ?? [], cartesia ?? [], 0.00004m, calls ?? [], trackedSince,
            media, media is null ? "down" : null, livekitPrice, payments ?? []);

    private static ProviderCallStat Call(string provider, DateTime hour, long ok, long quota = 0, long rateLimited = 0, long clientError = 0, string buckets = "{}")
        => new()
        {
            Provider = provider, HourStart = hour, Operation = "translation", Model = "gpt-4.1",
            Ok = ok, Quota = quota, RateLimited = rateLimited, ClientError = clientError,
            LatencyBuckets = buckets, LatencyCount = WarpTalk.BillingService.Domain.Services.ProviderCallStatMerge.ParseBuckets(buckets).Values.Sum(),
        };

    private static ConsumptionSlotRow Slot(string provider, DateTime at, long credits, long covered, decimal costUsd, string type = "STT")
        => new(at, type, provider, null, credits, 1, covered, covered > 0 ? 1 : 0, costUsd, 0);

    [Fact]
    public void Openai_cost_is_the_covered_ledger_cost_in_usd_and_in_vnd_at_the_days_rate()
    {
        var input = Inputs(ProviderCatalog.OpenAi, slots:
        [
            Slot(ProviderCatalog.OpenAi, Now.AddHours(-2), 100, 100, 0.5m),
            Slot(ProviderCatalog.OpenAi, Now.AddHours(-1), 300, 0, 0m, "TRANSLATION"),
            Slot(ProviderCatalog.Cartesia, Now.AddHours(-1), 999, 999, 9m, "AUDIO_DUBBING_STANDARD"),
        ]);
        var atoms = Atoms(input, Now.AddDays(-1), Now.AddHours(1));

        var figures = Window(input, atoms, Now.AddDays(-1), Now.AddHours(1));

        figures.Usage.Should().Be(400, "OpenAI usage is the WarpTalk credits of OpenAI-served charge types");
        figures.CostUsd.Should().Be(0.5m);
        figures.CostVnd.Should().Be(12_500m);
        Coverage(figures).Should().Be(25m);
        NoteOf(input, figures, Metrics.CostUsd).Should().Contain("covers 25% of OpenAI credits");
    }

    [Fact]
    public void Success_rate_leaves_client_errors_out_and_hours_before_tracking_are_gaps()
    {
        var since = Now.AddHours(-3);
        var input = Inputs(ProviderCatalog.OpenAi, trackedSince: since, calls:
        [
            Call(ProviderCatalog.OpenAi, HourOf(Now.AddHours(-2)), ok: 90, rateLimited: 8, quota: 2, clientError: 50),
        ]);
        var atoms = Atoms(input, Now.AddDays(-1), Now.AddHours(1));

        var tracked = Window(input, atoms, Now.AddHours(-3), Now.AddHours(1));
        tracked.Calls.Should().Be(150);
        tracked.Failures.Should().Be(10);
        tracked.SuccessRate.Should().Be(90m);
        tracked.ErrorRate.Should().Be(10m);
        tracked.FailuresByClass.Should().BeEquivalentTo(new Dictionary<string, long> { ["rate_limited"] = 8, ["quota"] = 2 });

        var before = Window(input, atoms, Now.AddDays(-1), Now.AddHours(-4));
        before.Calls.Should().BeNull("hours before the first recorded call are not tracked, not zero");
        before.SuccessRate.Should().BeNull();
    }

    [Fact]
    public void A_tracked_hour_with_no_calls_is_zero_calls_and_no_rate()
    {
        var input = Inputs(ProviderCatalog.OpenAi, trackedSince: Now.AddDays(-2));
        var atoms = Atoms(input, Now.AddDays(-1), Now.AddHours(1));

        var figures = Window(input, atoms, Now.AddHours(-5), Now.AddHours(-4));

        figures.Calls.Should().Be(0);
        figures.SuccessRate.Should().BeNull();
    }

    [Fact]
    public void Latency_percentiles_interpolate_inside_the_bucket_like_histogram_quantile()
    {
        var buckets = new Dictionary<string, long> { ["100"] = 50, ["250"] = 40, ["500"] = 10 };

        Percentile(buckets, 0.5).Should().Be(100);
        Percentile(buckets, 0.95).Should().Be(375);
        Percentile(new Dictionary<string, long> { ["1000"] = 1, ["+Inf"] = 99 }, 0.95).Should().Be(1000, "the +Inf bucket reports the last finite edge");
        Percentile(new Dictionary<string, long>(), 0.5).Should().BeNull();
    }

    [Fact]
    public void Cartesia_measured_credits_are_spread_over_the_utc_day_and_unsynced_days_are_gaps_with_a_rate_card_estimate()
    {
        var yesterday = DateOnly.FromDateTime(Now).AddDays(-1);
        var input = Inputs(ProviderCatalog.Cartesia,
            cartesia: [new CartesiaUsageDay(yesterday, 24_000, 0, Now)],
            slots: [Slot(ProviderCatalog.Cartesia, Now.AddDays(-3), 10, 10, 0.2m, "AUDIO_DUBBING_STANDARD")]);
        var atoms = Atoms(input, Now.AddDays(-4), Now.AddHours(1));

        var dayStart = yesterday.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var sixHours = Window(input, atoms, dayStart, dayStart.AddHours(6));
        sixHours.Usage.Should().Be(6_000);
        sixHours.CostUsd.Should().Be(0.24m);

        var unsynced = Window(input, atoms, Now.AddDays(-3).AddHours(-1), Now.AddDays(-3).AddHours(1));
        unsynced.Usage.Should().BeNull("a day Cartesia's usage API does not cover is a gap");
        unsynced.CostUsd.Should().Be(0.2m, "the rate-card estimate stands in for the cost");
        NoteOf(input, unsynced, Metrics.CostUsd).Should().Contain("estimated from rate cards");
    }

    [Fact]
    public void A_past_cartesia_day_read_before_it_closed_is_not_a_measurement()
    {
        var day = DateOnly.FromDateTime(Now).AddDays(-2);
        var readEarly = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        var input = Inputs(ProviderCatalog.Cartesia, cartesia: [new CartesiaUsageDay(day, 5_000, 0, readEarly)]);
        var atoms = Atoms(input, Now.AddDays(-3), Now);

        Window(input, atoms, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .Usage.Should().BeNull();
    }

    [Fact]
    public void Livekit_cost_needs_a_configured_price_and_unavailable_usage_is_null_with_the_reason()
    {
        var workspace = Guid.NewGuid();
        var media = new[] { new MediaUsageHourRow(HourOf(Now.AddHours(-1)), workspace, 3600, 7200, 1, 1, 10) };

        var priced = Inputs(ProviderCatalog.LiveKit, media: media, livekitPrice: 0.004m);
        var figures = Window(priced, Atoms(priced, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));
        figures.Usage.Should().Be(120m);
        figures.RoomMinutes.Should().Be(60m);
        figures.CostUsd.Should().Be(0.48m);

        var unpriced = Inputs(ProviderCatalog.LiveKit, media: media);
        var noPrice = Window(unpriced, Atoms(unpriced, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));
        noPrice.CostUsd.Should().BeNull();
        NoteOf(unpriced, noPrice, Metrics.CostUsd).Should().Contain("livekit_usd_per_participant_minute");

        var down = Inputs(ProviderCatalog.LiveKit);
        var unavailable = Window(down, Atoms(down, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));
        unavailable.Usage.Should().BeNull();
        NoteOf(down, unavailable, Metrics.Usage).Should().StartWith("LiveKit usage is unavailable");
    }

    [Fact]
    public void Stripe_counts_paid_and_failed_payments_and_has_no_cost()
    {
        var input = Inputs(ProviderCatalog.Stripe, payments:
        [
            new ProviderPaymentRow(Now.AddHours(-2), PaymentConstants.PaymentStatuses.Paid, "VND", 500_000m, Guid.NewGuid()),
            new ProviderPaymentRow(Now.AddHours(-2), PaymentConstants.PaymentStatuses.Paid, "USD", 10m, Guid.NewGuid()),
            new ProviderPaymentRow(Now.AddHours(-1), PaymentConstants.PaymentStatuses.Failed, "USD", 10m, Guid.NewGuid()),
        ]);
        var figures = Window(input, Atoms(input, Now.AddDays(-1), Now.AddHours(1)), Now.AddDays(-1), Now.AddHours(1));

        figures.Usage.Should().Be(2);
        figures.FailedPayments.Should().Be(1);
        figures.VolumeVnd.Should().Be(750_000m);
        figures.CostUsd.Should().BeNull();
        NoteOf(input, figures, Metrics.CostUsd).Should().Contain("fees are not synced");
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(3, 1, Degraded)]
    [InlineData(99, 1, Operational)]
    [InlineData(95, 5, Degraded)]
    [InlineData(70, 30, PartialOutage)]
    [InlineData(10, 90, MajorOutage)]
    public void Our_calls_imply_a_status(long ok, long failures, string? expected)
        => StatusOfCalls(ok, failures).Should().Be(expected);

    [Fact]
    public void The_worse_of_two_statuses_wins_and_no_signal_stays_null()
    {
        Worst(Operational, PartialOutage).Should().Be(PartialOutage);
        Worst(null, Degraded).Should().Be(Degraded);
        Worst(null, null).Should().BeNull();
        StatusOfImpact("critical").Should().Be(MajorOutage);
        StatusOfImpact("maintenance").Should().Be(Operational);
    }

    [Fact]
    public void Uptime_is_from_our_calls_when_there_are_any()
    {
        var days = AdminProvidersService.LastDays(Now, TimeZoneInfo.Utc, 3);
        var input = Inputs(ProviderCatalog.OpenAi, trackedSince: days[0].Start, calls:
        [
            Call(ProviderCatalog.OpenAi, days[0].Start.AddHours(3), ok: 999, quota: 1),
            Call(ProviderCatalog.OpenAi, days[1].Start.AddHours(3), ok: 60, rateLimited: 40),
        ]);
        var atoms = Atoms(input, days[0].Start, days[^1].End);

        var uptime = Uptime(input, atoms, days, [], statusPageCoveredFrom: null);

        uptime.Basis.Should().Be("calls");
        uptime.Percent.Should().Be(Math.Round(1059m * 100m / 1100m, 2));
        uptime.Days.Select(d => d.Status).Should().Equal(Operational, PartialOutage, NoData);
        uptime.Days[1].FailuresByClass.Should().ContainKey("rate_limited");
    }

    [Fact]
    public void Without_calls_uptime_comes_from_major_incidents_on_the_status_page_and_colours_the_day()
    {
        var days = AdminProvidersService.LastDays(Now, TimeZoneInfo.Utc, 2);
        var incident = new ProviderStatusIncident
        {
            Name = "API outage", Impact = "major", Status = "resolved",
            StartedAt = days[0].Start.AddHours(1), ResolvedAt = days[0].Start.AddHours(4),
        };
        var input = Inputs(ProviderCatalog.LiveKit, media: []);
        var atoms = Atoms(input, days[0].Start, days[^1].End);

        var uptime = Uptime(input, atoms, days, [incident], statusPageCoveredFrom: days[0].Start);

        uptime.Basis.Should().Be("statusPage");
        var window = Now - days[0].Start;
        uptime.Percent.Should().Be(Math.Round(100m - (decimal)(3.0 / window.TotalHours) * 100m, 2));
        uptime.Days[0].Status.Should().Be(PartialOutage);
        uptime.Days[0].Incidents.Should().ContainSingle().Which.Name.Should().Be("API outage");
        uptime.Days[1].Status.Should().Be(Operational, "the status page covered the day and reported nothing");
    }

    [Fact]
    public void With_no_signal_at_all_uptime_is_null_not_a_flattering_hundred()
    {
        var days = AdminProvidersService.LastDays(Now, TimeZoneInfo.Utc, 5);
        var input = Inputs(ProviderCatalog.Stripe);

        var uptime = Uptime(input, Atoms(input, days[0].Start, days[^1].End), days, [], null);

        uptime.Percent.Should().BeNull();
        uptime.Basis.Should().Be("none");
        uptime.Days.Should().OnlyContain(d => d.Status == NoData);
    }

    [Fact]
    public void Overlapping_incidents_are_counted_once()
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var incidents = new[]
        {
            new ProviderStatusIncident { Impact = "major", StartedAt = start.AddHours(1), ResolvedAt = start.AddHours(3) },
            new ProviderStatusIncident { Impact = "critical", StartedAt = start.AddHours(2), ResolvedAt = start.AddHours(4) },
            new ProviderStatusIncident { Impact = "minor", StartedAt = start.AddHours(5), ResolvedAt = start.AddHours(9) },
        };

        MergedDowntime(incidents, start, start.AddDays(1), start.AddDays(2)).Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public void Every_provider_offers_its_metrics_and_only_ai_providers_offer_calls()
    {
        MetricsOf(ProviderCatalog.OpenAi).Select(m => m.Key).Should().Contain([Metrics.Calls, Metrics.ErrorRate, Metrics.P95]);
        MetricsOf(ProviderCatalog.LiveKit).Select(m => m.Key).Should().NotContain(Metrics.Calls);
        MetricsOf(ProviderCatalog.Stripe).Select(m => m.Key).Should().Contain(Metrics.VolumeVnd);
        ProviderCatalog.All.Select(p => p.Key).Should().Equal(ProviderCatalog.OpenAi, ProviderCatalog.Cartesia, ProviderCatalog.LiveKit, ProviderCatalog.Stripe);
    }
}

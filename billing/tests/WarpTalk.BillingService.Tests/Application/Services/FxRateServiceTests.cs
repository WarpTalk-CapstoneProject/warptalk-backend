using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// The FX rate is Stripe's by default, recorded per day, never silently stale, and an admin override is
/// explicit and reversible.
/// </summary>
public sealed class FxRateServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private readonly List<FxRate> _rows = new();
    private readonly Dictionary<string, decimal> _config = new() { [FxRateConstants.RateConfigKey] = 26_300m };
    private readonly Mock<IStripeFxClient> _stripe = new();
    private readonly FxRateService _service;

    public FxRateServiceTests()
    {
        var repository = new Mock<IFxRateRepository>();
        repository.Setup(r => r.GetPairAsync("USD", "VND", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _rows.ToList());
        repository.Setup(r => r.UpsertAsync(It.IsAny<FxRate>(), It.IsAny<CancellationToken>()))
            .Callback<FxRate, CancellationToken>((rate, _) =>
            {
                _rows.RemoveAll(row => row.RateDate == rate.RateDate && row.Source == rate.Source);
                _rows.Add(rate);
            })
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.DeleteAsync("USD", "VND", It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateOnly, string, CancellationToken>((_, _, day, source, _) =>
                _rows.RemoveAll(row => row.RateDate == day && row.Source == source))
            .Returns(Task.CompletedTask);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.FxRates).Returns(repository.Object);

        var config = new Mock<IUsageRateCardRepository>();
        config.Setup(c => c.ReadPricingConfigValueAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, decimal fallback, CancellationToken _) => _config.TryGetValue(key, out var value) ? value : fallback);
        config.Setup(c => c.UpsertPricingConfigValueAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Callback<string, decimal, CancellationToken>((key, value, _) => _config[key] = value)
            .Returns(Task.CompletedTask);

        _stripe.SetupGet(s => s.IsConfigured).Returns(true);
        _stripe.Setup(s => s.GetUsdToVndQuoteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeFxQuote("fxq_1", Now, 25_989.9m, 25_730m, 0.01m));
        _stripe.Setup(s => s.GetVndChargeConversionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new StripeChargeConversion("txn_a", new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc), 100m / 0.00384405m),
                new StripeChargeConversion("txn_b", new DateTime(2026, 9, 23, 2, 0, 0, DateTimeKind.Utc), 100m / 0.0038438m),
            ]);

        _service = new FxRateService(unitOfWork.Object, config.Object, _stripe.Object, NullLogger<FxRateService>.Instance, new FixedTime(Now));
    }

    [Fact]
    public async Task Refresh_records_todays_Stripe_quote_and_past_charge_days_and_replaces_the_hardcoded_rate()
    {
        var result = await _service.RefreshAsync(force: false);

        result.Error.Should().BeNull();
        result.QuoteRecorded.Should().BeTrue();
        result.ChargeDaysRecorded.Should().Be(2);
        _rows.Should().ContainSingle(r => r.Source == FxRateConstants.Sources.StripeFxQuote && r.RateDate == Today && r.Rate == 25_989.9m && r.FeeInclusiveRate == 25_730m);
        _rows.Should().Contain(r => r.Source == FxRateConstants.Sources.StripeCharge && r.RateDate == new DateOnly(2026, 9, 22));

        // The single number other readers take follows Stripe; 26,300 is gone.
        _config[FxRateConstants.RateConfigKey].Should().Be(25_989.9m);
        result.Status.Source.Should().Be(FxRateConstants.Sources.StripeFxQuote);
        result.Status.AsOf.Should().Be(Now);
        result.Status.Stale.Should().BeFalse();
        result.Status.Warning.Should().BeNull();
    }

    [Fact]
    public async Task The_first_refresh_backfills_charges_far_back_and_later_ones_only_a_few_days()
    {
        await _service.RefreshAsync(force: false);
        await _service.RefreshAsync(force: true);

        _stripe.Verify(s => s.GetVndChargeConversionsAsync(Now.AddDays(-FxRateService.BackfillDays), It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.GetVndChargeConversionsAsync(Now.AddDays(-FxRateService.RecentDays), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_known_rate_with_a_visible_warning()
    {
        _rows.Add(new FxRate
        {
            BaseCurrency = "USD", QuoteCurrency = "VND", RateDate = Today.AddDays(-3), Rate = 25_900m,
            Source = FxRateConstants.Sources.StripeFxQuote, FetchedAt = Now.AddDays(-3),
        });
        _stripe.Setup(s => s.GetUsdToVndQuoteAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("timeout"));
        _stripe.Setup(s => s.GetVndChargeConversionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await _service.RefreshAsync(force: false);

        result.Error.Should().Contain("Stripe FX quote failed");
        result.Status.Rate.Should().Be(25_900m);
        result.Status.Basis.Should().Be("carriedForward");
        result.Status.Stale.Should().BeTrue();
        result.Status.Warning.Should().Contain("has not returned a rate since 2026-09-21").And.Contain("25,900");
    }

    [Fact]
    public async Task No_Stripe_rate_ever_is_a_warning_too_not_a_silent_26300()
    {
        var status = await _service.GetStatusAsync();

        status.Rate.Should().Be(26_300m);
        status.Source.Should().Be(FxRateConstants.Sources.Configured);
        status.Stale.Should().BeTrue();
        status.Warning.Should().Contain("No Stripe rate has been recorded yet");
    }

    [Fact]
    public async Task An_override_is_explicit_applies_from_today_and_is_reversible()
    {
        await _service.RefreshAsync(force: false);

        var set = await _service.SetOverrideAsync(27_000m);
        set.IsSuccess.Should().BeTrue();
        set.Value!.Mode.Should().Be("manual");
        set.Value.Rate.Should().Be(27_000m);
        set.Value.Stale.Should().BeFalse("an override is never stale");

        // A refresh during the override still records Stripe (for history), but does not overwrite the admin's number.
        await _service.RefreshAsync(force: true);
        _config[FxRateConstants.RateConfigKey].Should().Be(27_000m);
        (await _service.GetStatusAsync()).Rate.Should().Be(27_000m);

        var cleared = await _service.ClearOverrideAsync();
        cleared.Value!.Mode.Should().Be("stripe");
        cleared.Value.Rate.Should().Be(25_989.9m);
        _rows.Should().NotContain(r => r.Source == FxRateConstants.Sources.Manual && r.RateDate == Today);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_000_000)]
    public async Task An_implausible_override_is_refused(decimal rate)
    {
        var result = await _service.SetOverrideAsync(rate);
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}

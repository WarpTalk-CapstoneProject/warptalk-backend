using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Tests.Application.Providers;

public sealed class AdminProvidersServiceTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ICreditTransactionRepository> _transactions = new();
    private readonly Mock<IProviderCallStatRepository> _calls = new();
    private readonly Mock<IProviderStatusIncidentRepository> _incidents = new();
    private readonly Mock<IFxRateRepository> _fx = new();
    private readonly Mock<IUsageRateCardRepository> _pricing = new();
    private readonly Mock<IMediaUsageClient> _media = new();

    private static readonly DateTime Now = new(2026, 9, 25, 3, 0, 0, DateTimeKind.Utc);

    public AdminProvidersServiceTests()
    {
        _unitOfWork.SetupGet(u => u.CreditTransactionRepository).Returns(_transactions.Object);
        _unitOfWork.SetupGet(u => u.ProviderCallStats).Returns(_calls.Object);
        _unitOfWork.SetupGet(u => u.ProviderStatusIncidents).Returns(_incidents.Object);
        _unitOfWork.SetupGet(u => u.FxRates).Returns(_fx.Object);
        _transactions.Setup(t => t.GetConsumptionSlotsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _calls.Setup(c => c.GetRangeAsync(It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _incidents.Setup(i => i.GetOverlappingAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _fx.Setup(f => f.GetPairAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FxRate>());
        _pricing.Setup(p => p.ReadPricingConfigValueAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, decimal fallback, CancellationToken _) => fallback);
    }

    private AdminProvidersService Service() => new(
        _unitOfWork.Object,
        _pricing.Object,
        Mock.Of<IWorkspaceClient>(),
        NullLogger<AdminProvidersService>.Instance,
        new AdminProvidersOptions(),
        _media.Object,
        timeProvider: new FixedTime(Now));

    private static AdminInsightsQuery Range(int days) => new() { From = Now.AddDays(-days), To = Now };

    [Fact]
    public async Task An_unknown_provider_is_not_found()
    {
        (await Service().GetSeriesAsync("anthropic", Range(7), null, null)).ErrorCode.Should().Be(ErrorCodes.NotFound);
        (await Service().GetUptimeAsync("anthropic", null, null)).ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Hourly_series_are_limited_and_unknown_metrics_or_splits_are_refused()
    {
        (await Service().GetSeriesAsync("openai", Range(30), "hour", null)).ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await Service().GetSeriesAsync("openai", Range(7), "week", null)).ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await Service().GetSeriesAsync("livekit", Range(7), null, "calls")).ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await Service().GetBreakdownAsync("openai", Range(7), "planet")).ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await Service().GetUptimeAsync("openai", 91, null)).ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task A_livekit_series_says_why_it_is_empty_when_translation_room_is_down()
    {
        _media.Setup(m => m.GetHourlyAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<MediaUsageHourRow>>("translation-room did not answer", ErrorCodes.ServiceUnavailable));

        var result = await Service().GetSeriesAsync("livekit", Range(7), "day", "usage");

        result.IsSuccess.Should().BeTrue();
        var usage = result.Value!.Metrics.Should().ContainSingle().Subject;
        usage.Values.Should().OnlyContain(v => v == null);
        usage.Note.Should().Contain("translation-room did not answer");
    }

    [Fact]
    public async Task Openai_series_buckets_are_local_days_and_calls_before_tracking_are_gaps()
    {
        _calls.Setup(c => c.GetFirstHourAsync("openai", It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);

        var result = await Service().GetSeriesAsync("openai", Range(7), null, "usage,calls");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Buckets.Should().HaveCount(8, "seven days back from 10:00 in Hanoi touches eight local days");
        var metrics = result.Value.Metrics.ToDictionary(m => m.Key);
        metrics["usage"].Values.Should().OnlyContain(v => v == 0m);
        metrics["calls"].Values.Should().OnlyContain(v => v == null);
        metrics["calls"].Note.Should().Contain("no call to this provider has been recorded yet");
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }
}

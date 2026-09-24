using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="IFxRateService"/>
public sealed class FxRateService : IFxRateService
{
    /// <summary>In Stripe mode, a newest Stripe rate older than this is stale: the daily refresh missed a day.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);

    /// <summary>How far back the first refresh reads converted charges, to date past periods.</summary>
    public const int BackfillDays = 180;

    /// <summary>How far back every later refresh re-reads converted charges.</summary>
    public const int RecentDays = 3;

    public const int HistoryDays = 60;

    /// <summary>Above this a "VND per dollar" is a typo (the rate is ~26,000), not a rate.</summary>
    public const decimal MaxPlausibleRate = 1_000_000m;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IUsageRateCardRepository _config;
    private readonly IStripeFxClient _stripe;
    private readonly ILogger<FxRateService> _logger;
    private readonly TimeProvider _time;

    public FxRateService(
        IUnitOfWork unitOfWork,
        IUsageRateCardRepository config,
        IStripeFxClient stripe,
        ILogger<FxRateService> logger,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _config = config;
        _stripe = stripe;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<FxRateTable> GetTableAsync(CancellationToken ct = default)
    {
        var rows = await _unitOfWork.FxRates.GetPairAsync(FxRateConstants.Usd, FxRateConstants.Vnd, ct);
        var configured = await _config.ReadPricingConfigValueAsync(FxRateConstants.RateConfigKey, 0m, ct);
        return FxRateTable.From(rows, configured > 0 ? configured : null);
    }

    public async Task<AdminFxRateStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var now = Now;
        var rows = await _unitOfWork.FxRates.GetPairAsync(FxRateConstants.Usd, FxRateConstants.Vnd, ct);
        var configured = await _config.ReadPricingConfigValueAsync(FxRateConstants.RateConfigKey, 0m, ct);
        var manual = await IsManualAsync(ct);
        return BuildStatus(rows, configured > 0 ? configured : null, manual, now);
    }

    /// <summary>Pure: the status of a rate history at <paramref name="now"/>. Public for tests.</summary>
    public static AdminFxRateStatusDto BuildStatus(IReadOnlyList<FxRate> rows, decimal? configured, bool manual, DateTime now)
    {
        var table = FxRateTable.From(rows, configured);
        var today = DateOnly.FromDateTime(now);
        var resolved = table.Resolve(today);

        // While an override is on, today is the admin's number even before today's manual row exists.
        if (manual && configured is { } manualRate && resolved.Source != FxRateConstants.Sources.Manual)
        {
            resolved = new FxRateResolution(today, manualRate, FxRateConstants.Sources.Manual, today, FxRateBasis.Exact, null);
        }

        var latestStripe = rows
            .Where(row => row.Source is FxRateConstants.Sources.StripeFxQuote or FxRateConstants.Sources.StripeCharge)
            .OrderByDescending(row => row.RateDate)
            .ThenBy(row => FxRateConstants.Precedence(row.Source))
            .ThenByDescending(row => row.FetchedAt)
            .FirstOrDefault();

        var stale = !manual && (latestStripe is null
                                || now - DateTime.SpecifyKind(latestStripe.FetchedAt, DateTimeKind.Utc) > StaleAfter
                                || latestStripe.RateDate < today.AddDays(-1));
        string? warning = null;
        if (stale)
        {
            warning = latestStripe is null
                ? resolved.Rate is { } fallback
                    ? string.Create(Invariant, $"No Stripe rate has been recorded yet; reports use the configured {fallback:N0} VND/USD until the first refresh succeeds.")
                    : "No Stripe rate has been recorded yet and no rate is configured; USD amounts cannot be converted."
                : string.Create(Invariant,
                    $"Stripe has not returned a rate since {latestStripe.RateDate:yyyy-MM-dd}; reports use the last known rate ({resolved.Rate:N0} VND/USD, {FxRateTable.SourceLabel(resolved.Source)}{(resolved.RateDate is { } d ? $", {d:yyyy-MM-dd}" : "")}).");
        }

        var history = table.Recorded(today.AddDays(-(HistoryDays - 1)), today)
            .Select(day => new AdminFxRateDayDto(day.Day.ToString("yyyy-MM-dd", Invariant), day.Rate!.Value, day.Source))
            .ToList();

        return new AdminFxRateStatusDto(
            FxRateConstants.Usd,
            FxRateConstants.Vnd,
            resolved.Rate,
            resolved.Source,
            FxRateTable.SourceLabel(resolved.Source),
            resolved.RateDate?.ToString("yyyy-MM-dd", Invariant),
            resolved.FetchedAt,
            BasisName(resolved.Basis),
            manual ? "manual" : "stripe",
            manual ? configured : null,
            latestStripe is null
                ? null
                : new AdminFxStripeRateDto(
                    latestStripe.Rate,
                    latestStripe.FeeInclusiveRate,
                    latestStripe.Source,
                    latestStripe.RateDate.ToString("yyyy-MM-dd", Invariant),
                    DateTime.SpecifyKind(latestStripe.FetchedAt, DateTimeKind.Utc),
                    latestStripe.SourceRef),
            stale,
            warning,
            history);
    }

    public async Task<AdminFxRefreshResultDto> RefreshAsync(bool force, CancellationToken ct = default)
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var rows = await _unitOfWork.FxRates.GetPairAsync(FxRateConstants.Usd, FxRateConstants.Vnd, ct);
        var manual = await IsManualAsync(ct);
        var errors = new List<string>();
        var quoteRecorded = false;
        var chargeDays = 0;

        if (manual)
        {
            var manualRate = await _config.ReadPricingConfigValueAsync(FxRateConstants.RateConfigKey, 0m, ct);
            if (manualRate > 0) await UpsertAsync(today, manualRate, FxRateConstants.Sources.Manual, null, null, now, ct);
        }

        if (!_stripe.IsConfigured)
        {
            errors.Add("no Stripe secret key is configured");
        }
        else
        {
            var hasQuoteToday = rows.Any(row => row.RateDate == today && row.Source == FxRateConstants.Sources.StripeFxQuote);
            if (force || !hasQuoteToday)
            {
                try
                {
                    var quote = await _stripe.GetUsdToVndQuoteAsync(ct);
                    if (IsPlausible(quote.BaseRate))
                    {
                        await UpsertAsync(today, quote.BaseRate, FxRateConstants.Sources.StripeFxQuote,
                            IsPlausible(quote.ExchangeRate) ? quote.ExchangeRate : null, quote.Id, now, ct);
                        quoteRecorded = true;
                    }
                    else
                    {
                        errors.Add(string.Create(Invariant, $"Stripe FX quote returned an implausible rate ({quote.BaseRate})"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    errors.Add($"Stripe FX quote failed: {Describe(ex)}");
                    _logger.LogWarning(ex, "Stripe FX quote failed; reports keep the last known USD→VND rate.");
                }
            }

            try
            {
                var hasCharges = rows.Any(row => row.Source == FxRateConstants.Sources.StripeCharge);
                var since = now.AddDays(-(hasCharges ? RecentDays : BackfillDays));
                var conversions = await _stripe.GetVndChargeConversionsAsync(since, ct);
                foreach (var day in conversions
                             .Where(c => IsPlausible(c.VndPerUsd))
                             .GroupBy(c => DateOnly.FromDateTime(c.CreatedAt)))
                {
                    // The day's last conversion: the rate Stripe was applying at the end of it.
                    var last = day.OrderBy(c => c.CreatedAt).Last();
                    await UpsertAsync(day.Key, Math.Round(last.VndPerUsd, 4, MidpointRounding.AwayFromZero),
                        FxRateConstants.Sources.StripeCharge, null, last.BalanceTransactionId, now, ct);
                    chargeDays++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"Stripe charge conversions failed: {Describe(ex)}");
                _logger.LogWarning(ex, "Reading Stripe charge conversions failed.");
            }
        }

        await SyncConfiguredRateAsync(ct);
        return new AdminFxRefreshResultDto(
            quoteRecorded,
            chargeDays,
            errors.Count == 0 ? null : string.Join("; ", errors),
            await GetStatusAsync(ct));
    }

    public async Task<Result<AdminFxRateStatusDto>> SetOverrideAsync(decimal rate, CancellationToken ct = default)
    {
        if (!IsPlausible(rate))
        {
            return Result.Failure<AdminFxRateStatusDto>(
                "The override must be a positive number of VND per US dollar.", ErrorCodes.ValidationError);
        }

        var now = Now;
        await _config.UpsertPricingConfigValueAsync(FxRateConstants.RateConfigKey, rate, ct);
        await _config.UpsertPricingConfigValueAsync(FxRateConstants.ManualConfigKey, 1m, ct);
        await UpsertAsync(DateOnly.FromDateTime(now), rate, FxRateConstants.Sources.Manual, null, null, now, ct);
        _logger.LogInformation("USD→VND rate overridden to {Rate} from {Date:yyyy-MM-dd}.", rate, now);
        return Result.Success(await GetStatusAsync(ct));
    }

    public async Task<Result<AdminFxRateStatusDto>> ClearOverrideAsync(CancellationToken ct = default)
    {
        var now = Now;
        await _config.UpsertPricingConfigValueAsync(FxRateConstants.ManualConfigKey, 0m, ct);
        // Today goes back to Stripe; the days the override was on keep it, so past reports do not move.
        await _unitOfWork.FxRates.DeleteAsync(
            FxRateConstants.Usd, FxRateConstants.Vnd, DateOnly.FromDateTime(now), FxRateConstants.Sources.Manual, ct);
        await SyncConfiguredRateAsync(ct);
        _logger.LogInformation("USD→VND override cleared; Stripe's rate applies from {Date:yyyy-MM-dd}.", now);
        return Result.Success(await GetStatusAsync(ct));
    }

    /// <summary>Keeps fx_rate_usd_vnd — what single-rate readers use — equal to today's effective Stripe rate.</summary>
    private async Task SyncConfiguredRateAsync(CancellationToken ct)
    {
        if (await IsManualAsync(ct)) return;

        var rows = await _unitOfWork.FxRates.GetPairAsync(FxRateConstants.Usd, FxRateConstants.Vnd, ct);
        var stripeOnly = rows.Where(row => row.Source != FxRateConstants.Sources.Manual).ToList();
        if (stripeOnly.Count == 0) return;

        var today = FxRateTable.From(stripeOnly, null).Resolve(DateOnly.FromDateTime(Now));
        if (today.Rate is { } rate)
        {
            await _config.UpsertPricingConfigValueAsync(FxRateConstants.RateConfigKey, rate, ct);
        }
    }

    private async Task<bool> IsManualAsync(CancellationToken ct)
        => await _config.ReadPricingConfigValueAsync(FxRateConstants.ManualConfigKey, 0m, ct) >= 1m;

    private Task UpsertAsync(DateOnly day, decimal rate, string source, decimal? feeInclusive, string? sourceRef, DateTime now, CancellationToken ct)
        => _unitOfWork.FxRates.UpsertAsync(new FxRate
        {
            BaseCurrency = FxRateConstants.Usd,
            QuoteCurrency = FxRateConstants.Vnd,
            RateDate = day,
            Rate = rate,
            Source = source,
            FeeInclusiveRate = feeInclusive,
            SourceRef = sourceRef,
            FetchedAt = now,
        }, ct);

    public static bool IsPlausible(decimal rate) => rate > 0 && rate < MaxPlausibleRate;

    private static string BasisName(FxRateBasis basis) => basis switch
    {
        FxRateBasis.Exact => "exact",
        FxRateBasis.CarriedForward => "carriedForward",
        FxRateBasis.BeforeFirstRecord => "beforeFirstRecord",
        FxRateBasis.Configured => "configured",
        _ => "none",
    };

    /// <summary>The exception's type and message. Stripe's messages never carry the key.</summary>
    private static string Describe(Exception ex)
    {
        var message = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
        return $"{ex.GetType().Name}: {message}";
    }
}

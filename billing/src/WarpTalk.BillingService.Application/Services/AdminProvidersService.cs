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
using WarpTalk.Shared.Contracts.Admin;
using static WarpTalk.BillingService.Application.Services.ProviderMetricsCalculator;

namespace WarpTalk.BillingService.Application.Services;

/// <inheritdoc cref="IAdminProvidersService"/>
public sealed class AdminProvidersService : IAdminProvidersService
{
    /// <summary>billing_pricing_config key: USD per LiveKit participant minute. Absent = LiveKit cost unavailable.</summary>
    public const string LiveKitUsdPerParticipantMinuteKey = "livekit_usd_per_participant_minute";

    public const int UptimeDays = 90;
    public const int MaxSeriesDays = 120;
    public const int MaxHourlyDays = 14;
    public const int TopItems = 10;

    private static readonly string[] BreakdownKinds = ["workspace", "service", "model", "operation", "errorClass"];
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IUsageRateCardRepository _pricingConfig;
    private readonly IWorkspaceClient _workspaceClient;
    private readonly IMediaUsageClient? _media;
    private readonly IProviderStatusPageState? _statusPages;
    private readonly ICartesiaUsageSyncStatus? _cartesiaSync;
    private readonly IFxRateService? _fx;
    private readonly AdminProvidersOptions _options;
    private readonly ILogger<AdminProvidersService> _logger;
    private readonly TimeProvider _time;

    public AdminProvidersService(
        IUnitOfWork unitOfWork,
        IUsageRateCardRepository pricingConfig,
        IWorkspaceClient workspaceClient,
        ILogger<AdminProvidersService> logger,
        AdminProvidersOptions? options = null,
        IMediaUsageClient? media = null,
        IProviderStatusPageState? statusPages = null,
        ICartesiaUsageSyncStatus? cartesiaSync = null,
        IFxRateService? fx = null,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _pricingConfig = pricingConfig;
        _workspaceClient = workspaceClient;
        _logger = logger;
        _options = options ?? new AdminProvidersOptions();
        _media = media;
        _statusPages = statusPages;
        _cartesiaSync = cartesiaSync;
        _fx = fx;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ── overview ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminProvidersOverviewDto>> GetOverviewAsync(string? timeZoneId, CancellationToken ct = default)
    {
        if (!AdminComparisonRange.TryResolveTimeZone(timeZoneId, out var timeZone, out var error))
        {
            return Result.Failure<AdminProvidersOverviewDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var days = LastDays(now, timeZone, UptimeDays);
            var from = days[0].Start < now.AddHours(-24) ? days[0].Start : now.AddHours(-24);
            var shared = await ReadSharedAsync(from, now.AddHours(1), ct);

            var providers = new List<AdminProviderSummaryDto>();
            foreach (var info in ProviderCatalog.All)
            {
                var input = await ReadProviderAsync(info.Key, from, now.AddHours(1), now, shared, ct);
                var atoms = Atoms(input, from, now.AddHours(1));
                var today = Window(input, atoms, days[^1].Start, days[^1].End);
                var last24 = Window(input, atoms, now.AddHours(-24), now.AddHours(1));
                var lastHour = Window(input, atoms, now.AddHours(-1), now.AddHours(1));
                var incidents = await _unitOfWork.ProviderStatusIncidents.GetOverlappingAsync(info.Key, days[0].Start, now.AddHours(1), ct);
                var coveredFrom = await StatusPageCoveredFromAsync(info.Key, ct);
                var uptime = Uptime(input, atoms, days, incidents, coveredFrom);
                var page = await StatusPageAsync(info.Key, ct);

                var (status, source, note) = CurrentStatus(input, atoms, now, page, incidents);
                providers.Add(new AdminProviderSummaryDto(
                    info.Key,
                    info.Name,
                    info.Category,
                    info.Services,
                    status,
                    source,
                    note,
                    page,
                    new AdminProviderTodayDto(
                        info.UsageUnit,
                        today.Usage,
                        today.CostUsd,
                        today.CostVnd,
                        NoteOf(input, today, Metrics.Usage),
                        NoteOf(input, today, Metrics.CostUsd)),
                    new AdminProviderLiveDto(
                        last24.Calls,
                        last24.Failures,
                        last24.SuccessRate,
                        last24.ErrorRate,
                        last24.P50Ms,
                        last24.P95Ms,
                        lastHour.Calls,
                        NoteOf(input, last24, Metrics.Calls)),
                    new AdminProviderUptimeHeadlineDto(uptime.Percent, uptime.Basis, input.CallsTrackedSince, UptimeDays),
                    await ConfigAsync(info.Key, input, last24, page, ct)));
            }

            return Result.Success(new AdminProvidersOverviewDto(now, timeZone.Id, providers));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin providers overview failed.");
            return Result.Failure<AdminProvidersOverviewDto>("An unexpected error occurred while reading the providers.", ErrorCodes.InternalServerError);
        }
    }

    /// <summary>
    /// "Now": the worse of our calls in the last two hours (when there were enough to judge) and the
    /// status page's current indicator; unknown when neither says anything.
    /// </summary>
    private static (string Status, string Source, string? Note) CurrentStatus(
        ProviderInputs input,
        IReadOnlyDictionary<DateTime, Atom> atoms,
        DateTime now,
        AdminProviderStatusPageDto? page,
        IReadOnlyList<ProviderStatusIncident> incidents)
    {
        var recent = Window(input, atoms, now.AddHours(-2), now.AddHours(1));
        string? fromCalls = null;
        if (recent.Calls is not null && (recent.Calls - (recent.ClientErrors ?? 0)) >= MinCallsForRate)
        {
            var failures = recent.Failures ?? 0;
            fromCalls = StatusOfCalls((recent.Calls ?? 0) - failures - (recent.ClientErrors ?? 0), failures);
        }

        var open = incidents.Where(i => i.ResolvedAt is null).Select(i => StatusOfImpact(i.Impact)).Aggregate((string?)null, Worst);
        var fromPage = page?.Indicator is { } indicator ? Worst(StatusOfImpact(indicator), open) : open;

        var status = Worst(fromCalls, fromPage);
        var source = (fromCalls, fromPage) switch
        {
            (not null, not null) => "both",
            (not null, null) => "calls",
            (null, not null) => "statusPage",
            _ => "none",
        };

        string? note = null;
        if (fromCalls is not null && recent.ErrorRate is { } rate)
        {
            note = string.Create(Invariant, $"{rate:0.##}% of our calls failed in the last 2 h");
        }
        else if (fromCalls is null && input.Provider is ProviderCatalog.OpenAi or ProviderCatalog.Cartesia)
        {
            note = "too few calls in the last 2 h to judge from our own traffic";
        }

        return (status is null || Rank(status) < 0 ? Unknown : status, source, note);
    }

    private async Task<IReadOnlyList<AdminProviderConfigItemDto>> ConfigAsync(
        string provider, ProviderInputs input, WindowFigures last24, AdminProviderStatusPageDto? page, CancellationToken ct)
    {
        var items = new List<AdminProviderConfigItemDto>();
        static AdminProviderConfigItemDto Item(string key, string label, string value, string state) => new(key, label, value, state);

        switch (provider)
        {
            case ProviderCatalog.OpenAi:
            case ProviderCatalog.Cartesia:
            {
                // The AI workers hold the API key, not billing: whether it WORKS is what we can see.
                var ok = (last24.Calls ?? 0) - (last24.Failures ?? 0) - (last24.ClientErrors ?? 0);
                var auth = last24.FailuresByClass.GetValueOrDefault("auth");
                items.Add(auth > 0 && ok == 0
                    ? Item("apiKey", "API key", $"rejected ({auth} auth failures in 24 h)", "no")
                    : ok > 0
                        ? Item("apiKey", "API key", "in use (successful calls in the last 24 h)", "yes")
                        : Item("apiKey", "API key", "no call in the last 24 h to prove it", "info"));
                var quota = last24.FailuresByClass.GetValueOrDefault("quota");
                if (quota > 0) items.Add(Item("quota", "Quota / credits", $"{quota} call(s) refused for quota (402) in 24 h", "no"));
                items.Add(Item("callTracking", "Call tracking",
                    input.CallsTrackedSince is { } since ? "since " + since.ToString("yyyy-MM-dd HH:mm 'UTC'", Invariant) : "no call recorded yet", input.CallsTrackedSince is null ? "no" : "yes"));
                var models = input.Calls.Where(c => c.Provider == provider && c.HourStart >= _time.GetUtcNow().UtcDateTime.AddHours(-24) && c.Model != "-")
                    .Select(c => c.Model).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
                if (models.Count > 0) items.Add(Item("models", "Models called (24 h)", string.Join(", ", models), "info"));
                if (provider == ProviderCatalog.Cartesia)
                {
                    var sync = _cartesiaSync is null ? null : await _cartesiaSync.GetAsync(ct);
                    items.Add(Item("usageApi", "Usage API (admin key)",
                        sync is null ? "not configured" : sync.Status,
                        sync?.Status == ProviderUsageConstants.SyncStatuses.Ok ? "yes" : sync?.Status == ProviderUsageConstants.SyncStatuses.Disabled || sync is null ? "no" : "info"));
                    items.Add(Item("price", "USD per credit", input.CartesiaUsdPerCredit.ToString("0.#######", Invariant), "info"));
                    items.Add(Item("balance", "Credit balance", "not reported by Cartesia's API", "info"));
                }

                break;
            }
            case ProviderCatalog.LiveKit:
                items.Add(Item("usageSource", "Usage source", input.Media is null ? "translation-room unavailable" : "translation-room meetings (estimate)", input.Media is null ? "no" : "yes"));
                items.Add(Item("price", "USD per participant minute",
                    input.LiveKitUsdPerParticipantMinute is { } price ? price.ToString("0.######", Invariant) : "not configured",
                    input.LiveKitUsdPerParticipantMinute is null ? "no" : "yes"));
                break;
            case ProviderCatalog.Stripe:
                items.Add(Item("secretKey", "Secret key", _options.StripeSecretKeyConfigured ? "configured" : "not configured", _options.StripeSecretKeyConfigured ? "yes" : "no"));
                items.Add(Item("webhookSecret", "Webhook secret", _options.StripeWebhookSecretConfigured ? "configured" : "not configured", _options.StripeWebhookSecretConfigured ? "yes" : "no"));
                items.Add(Item("fees", "Processing fees", "not synced", "info"));
                break;
        }

        items.Add(page is null
            ? Item("statusPage", "Status page", provider == ProviderCatalog.Stripe ? "no statuspage.io API" : "not configured", "no")
            : Item("statusPage", "Status page", page.Url, page.Error is null ? "yes" : "info"));
        return items;
    }

    // ── series ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminProviderSeriesDto>> GetSeriesAsync(
        string provider, AdminInsightsQuery query, string? granularity, string? metrics, CancellationToken ct = default)
    {
        var info = ProviderCatalog.Find(provider);
        if (info is null) return Result.Failure<AdminProviderSeriesDto>($"Unknown provider '{provider}'.", ErrorCodes.NotFound);
        if (!AdminComparisonRange.TryResolve(query, out var window, out var error, MaxSeriesDays))
        {
            return Result.Failure<AdminProviderSeriesDto>(error!, ErrorCodes.ValidationError);
        }

        var grain = string.IsNullOrWhiteSpace(granularity) ? Granularities.Day : granularity.Trim().ToLowerInvariant();
        if (grain is not (Granularities.Day or Granularities.Hour))
        {
            return Result.Failure<AdminProviderSeriesDto>("Unknown granularity. Expected day or hour.", ErrorCodes.ValidationError);
        }

        if (grain == Granularities.Hour && window.To - window.From > TimeSpan.FromDays(MaxHourlyDays))
        {
            return Result.Failure<AdminProviderSeriesDto>($"Hourly series are limited to {MaxHourlyDays} days.", ErrorCodes.ValidationError);
        }

        var offered = MetricsOf(info.Key);
        var wanted = ParseMetrics(metrics);
        var unknown = wanted.Where(m => offered.All(o => o.Key != m)).ToList();
        if (unknown.Count > 0)
        {
            return Result.Failure<AdminProviderSeriesDto>(
                $"Unknown metric(s) for {info.Key}: {string.Join(", ", unknown)}. Expected: {string.Join(", ", offered.Select(o => o.Key))}.",
                ErrorCodes.ValidationError);
        }

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var shared = await ReadSharedAsync(window.From, window.To, ct);
            var input = await ReadProviderAsync(info.Key, window.From, window.To, now, shared, ct);
            var atoms = Atoms(input, window.From, window.To);

            var buckets = grain == Granularities.Day
                ? window.Days().Select(d => new AdminProviderBucketDto(d.Key, d.Start, d.End, d.Start > now)).ToList()
                : Hours(window.From, window.To, window.TimeZone, now);
            var perBucket = buckets.Select(b => b.Future ? null : Window(input, atoms, b.Start, b.End)).ToList();
            var total = Window(input, atoms, window.From, window.To);

            var selected = offered.Where(o => wanted.Count == 0 || wanted.Contains(o.Key)).ToList();
            var series = selected
                .Select(o => new AdminProviderMetricSeriesDto(
                    o.Key,
                    o.Unit,
                    perBucket.Select(f => f is null ? null : ValueOf(f, o.Key)).ToList(),
                    NoteOf(input, total, o.Key)))
                .ToList();
            var totals = selected
                .Select(o => new AdminProviderTotalDto(o.Key, o.Unit, ValueOf(total, o.Key), NoteOf(input, total, o.Key)))
                .ToList();
            if (info.Key is ProviderCatalog.OpenAi or ProviderCatalog.Cartesia)
            {
                totals.Add(new AdminProviderTotalDto("successRate", Units.Percent, total.SuccessRate, null));
                totals.Add(new AdminProviderTotalDto("p99Ms", Units.Ms, total.P99Ms, null));
                totals.Add(new AdminProviderTotalDto("clientErrors", Units.Count, total.ClientErrors, "requests WarpTalk got wrong (4xx other than 401/402/403/429); not counted against the provider"));
            }

            return Result.Success(new AdminProviderSeriesDto(info.Key, window.Range, grain, window.TimeZone.Id, buckets, series, totals));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin provider series failed. Provider: {Provider}", info.Key);
            return Result.Failure<AdminProviderSeriesDto>("An unexpected error occurred while reading the provider series.", ErrorCodes.InternalServerError);
        }
    }

    private static List<string> ParseMetrics(string? metrics)
        => string.IsNullOrWhiteSpace(metrics)
            ? []
            : metrics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    private static List<AdminProviderBucketDto> Hours(DateTime from, DateTime to, TimeZoneInfo timeZone, DateTime now)
    {
        var result = new List<AdminProviderBucketDto>();
        for (var start = HourOf(from); start < to; start = start.AddHours(1))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(start, timeZone);
            result.Add(new AdminProviderBucketDto(local.ToString("yyyy-MM-dd'T'HH:00", Invariant), start, start.AddHours(1), start > now));
        }

        return result;
    }

    // ── breakdown ─────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminProviderBreakdownDto>> GetBreakdownAsync(
        string provider, AdminInsightsQuery query, string? by, CancellationToken ct = default)
    {
        var info = ProviderCatalog.Find(provider);
        if (info is null) return Result.Failure<AdminProviderBreakdownDto>($"Unknown provider '{provider}'.", ErrorCodes.NotFound);
        if (!AdminComparisonRange.TryResolve(query, out var window, out var error, MaxSeriesDays))
        {
            return Result.Failure<AdminProviderBreakdownDto>(error!, ErrorCodes.ValidationError);
        }

        var kind = BreakdownKinds.FirstOrDefault(k => string.Equals(k, by?.Trim() ?? "workspace", StringComparison.OrdinalIgnoreCase));
        if (kind is null)
        {
            return Result.Failure<AdminProviderBreakdownDto>($"Unknown breakdown. Expected one of: {string.Join(", ", BreakdownKinds)}.", ErrorCodes.ValidationError);
        }

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var fx = await ReadFxTableAsync(ct);
            var result = kind switch
            {
                "workspace" => await ByWorkspaceAsync(info.Key, window.From, window.To, fx, ct),
                "service" => await ByServiceAsync(info.Key, window.From, window.To, fx, ct),
                "model" => await ByModelAsync(info.Key, window.From, window.To, now, ct),
                "operation" => await ByCallsAsync(info.Key, window.From, window.To, c => c.Operation, ct),
                _ => await ByErrorClassAsync(info.Key, window.From, window.To, ct),
            };

            return Result.Success(Finish(info.Key, kind, window.Range, result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin provider breakdown failed. Provider: {Provider} By: {By}", info.Key, kind);
            return Result.Failure<AdminProviderBreakdownDto>("An unexpected error occurred while reading the provider breakdown.", ErrorCodes.InternalServerError);
        }
    }

    private sealed record Slices(string Unit, bool Available, IReadOnlyList<AdminProviderBreakdownItemDto> Items, string? Note);

    private static Slices Unavailable(string unit, string note) => new(unit, false, [], note);

    /// <summary>Largest first, the tail folded into "Other" after <see cref="TopItems"/>, shares of the total.</summary>
    private static AdminProviderBreakdownDto Finish(string provider, string by, AdminInsightRange range, Slices slices)
    {
        var ordered = slices.Items.Where(i => i.Value > 0 || (i.Calls ?? 0) > 0).OrderByDescending(i => i.Value).ThenBy(i => i.Label, StringComparer.Ordinal).ToList();
        var total = ordered.Sum(i => i.Value);
        if (ordered.Count > TopItems)
        {
            var tail = ordered.Skip(TopItems - 1).ToList();
            ordered = ordered.Take(TopItems - 1).ToList();
            ordered.Add(new AdminProviderBreakdownItemDto(
                "other",
                $"Other ({tail.Count})",
                tail.Sum(i => i.Value),
                null,
                tail.All(i => i.CostUsd is null) ? null : tail.Sum(i => i.CostUsd ?? 0),
                tail.All(i => i.CostVnd is null) ? null : tail.Sum(i => i.CostVnd ?? 0),
                tail.All(i => i.Calls is null) ? null : tail.Sum(i => i.Calls ?? 0),
                tail.All(i => i.Failures is null) ? null : tail.Sum(i => i.Failures ?? 0)));
        }

        var items = ordered
            .Select(i => i with { Share = total > 0 ? Math.Round(i.Value * 100m / total, 1) : null })
            .ToList();
        var note = slices.Note ?? (slices.Available && items.Count == 0 ? "no usage recorded for this provider in the period" : null);
        return new AdminProviderBreakdownDto(provider, by, range, slices.Unit, slices.Available, total, items, note);
    }

    private async Task<Slices> ByWorkspaceAsync(string provider, DateTime from, DateTime to, FxRateTable fx, CancellationToken ct)
    {
        var fxDay = fx.Resolve(to.AddTicks(-1)).Rate;
        switch (provider)
        {
            case ProviderCatalog.OpenAi:
            case ProviderCatalog.Cartesia:
            {
                var rows = (await _unitOfWork.CreditTransactionRepository.GetProviderWorkspaceConsumptionAsync(from, to, ct))
                    .Where(r => r.Provider == provider)
                    .GroupBy(r => r.WorkspaceId)
                    .Select(g => (Workspace: g.Key, Credits: g.Sum(r => r.Credits), Cost: g.Sum(r => r.CostUsd)))
                    .ToList();
                var names = await NamesAsync(rows.Select(r => r.Workspace), ct);
                var items = rows.Select(r => new AdminProviderBreakdownItemDto(
                        r.Workspace.ToString(), NameOf(names, r.Workspace), r.Credits, null,
                        Math.Round(r.Cost, 6), fxDay is { } rate ? Math.Round(r.Cost * rate, 0) : null, null, null))
                    .ToList();
                var note = provider == ProviderCatalog.Cartesia
                    ? "WarpTalk credits charged per workspace; cost is the rate-card estimate (Cartesia does not split its usage by customer)"
                    : "WarpTalk credits charged per workspace; cost only where the rate card carries a provider price";
                return new Slices(Units.Credits, true, items, note + (fxDay is null ? "" : "; VND at the period's last USD→VND rate"));
            }
            case ProviderCatalog.LiveKit:
            {
                if (_media is null) return Unavailable(Units.Minutes, "LiveKit usage is unavailable: no translation-room client");
                var media = await _media.GetHourlyAsync(from, to, ct);
                if (!media.IsSuccess) return Unavailable(Units.Minutes, "LiveKit usage is unavailable: " + media.Error);
                var price = await LiveKitPriceAsync(ct);
                var rows = media.Value!.GroupBy(r => r.WorkspaceId)
                    .Select(g => (Workspace: g.Key, Minutes: (decimal)g.Sum(r => r.ParticipantSeconds) / 60m))
                    .ToList();
                var names = await NamesAsync(rows.Select(r => r.Workspace), ct);
                var items = rows.Select(r =>
                    {
                        decimal? cost = price is { } p ? Math.Round(r.Minutes * p, 6) : null;
                        return new AdminProviderBreakdownItemDto(
                            r.Workspace.ToString(), NameOf(names, r.Workspace), Math.Round(r.Minutes, 1), null,
                            cost, cost is { } usd && fxDay is { } rate ? Math.Round(usd * rate, 0) : null, null, null);
                    })
                    .ToList();
                return new Slices(Units.Minutes, true, items, "estimated participant minutes per workspace");
            }
            default:
            {
                var payments = await _unitOfWork.PaymentRepository.GetProviderPaymentsAsync(ProviderCatalog.Stripe, from, to, ct);
                var rows = payments.Where(p => p.Status != PaymentConstants.PaymentStatuses.Failed && p.WorkspaceId is not null)
                    .GroupBy(p => p.WorkspaceId!.Value)
                    .Select(g => (Workspace: g.Key, Vnd: g.Sum(p => ToVnd(p, fx) ?? 0m), Count: g.LongCount()))
                    .ToList();
                var names = await NamesAsync(rows.Select(r => r.Workspace), ct);
                var items = rows.Select(r => new AdminProviderBreakdownItemDto(
                        r.Workspace.ToString(), NameOf(names, r.Workspace), Math.Round(r.Vnd, 0), null, null, null, r.Count, null))
                    .ToList();
                return new Slices(Units.Vnd, true, items, "payment volume per workspace, VND (USD at the day's rate)");
            }
        }
    }

    private async Task<Slices> ByServiceAsync(string provider, DateTime from, DateTime to, FxRateTable fx, CancellationToken ct)
    {
        if (provider is not (ProviderCatalog.OpenAi or ProviderCatalog.Cartesia))
        {
            return Unavailable(Units.Credits, provider == ProviderCatalog.LiveKit
                ? "LiveKit is one service here (meetings); see recordings on the chart"
                : "Stripe is one service here (payments)");
        }

        var slots = (await _unitOfWork.CreditTransactionRepository.GetConsumptionSlotsAsync(from, to, ct))
            .Where(s => s.Provider == provider)
            .ToList();
        var items = slots
            .GroupBy(s => AiProviderCatalog.ServiceOf(s.ChargeType))
            .Select(g =>
            {
                var cost = g.Sum(s => s.CoveredCredits > 0 || s.CoveredTransactions > 0 ? s.CostUsd : 0m);
                var vnd = g.Sum(s => (s.CoveredCredits > 0 || s.CoveredTransactions > 0 ? s.CostUsd : 0m) * (fx.Resolve(s.SlotStart).Rate ?? 0m));
                return new AdminProviderBreakdownItemDto(
                    g.Key, g.Key, g.Sum(s => s.Credits), null, Math.Round(cost, 6), Math.Round(vnd, 0), g.Sum(s => (long)s.Transactions), null);
            })
            .ToList();
        return new Slices(Units.Credits, true, items, "WarpTalk credits per service; cost only where the rate card carries a provider price");
    }

    private async Task<Slices> ByModelAsync(string provider, DateTime from, DateTime to, DateTime now, CancellationToken ct)
    {
        if (provider == ProviderCatalog.Cartesia)
        {
            var models = await _unitOfWork.ProviderUsageDaily.GetDaysAsync(
                ProviderUsageConstants.Providers.Cartesia, ProviderUsageConstants.GroupKinds.Model,
                DateOnly.FromDateTime(from), DateOnly.FromDateTime(to.AddTicks(-1) < now ? to.AddTicks(-1) : now), ct);
            if (models.Count > 0)
            {
                var items = models.GroupBy(m => m.GroupId)
                    .Select(g => new AdminProviderBreakdownItemDto(g.Key, g.Last().GroupLabel ?? g.Key, g.Sum(m => m.Credits), null, null, null, null, null))
                    .ToList();
                return new Slices(Units.ProviderCredits, true, items, "credits Cartesia reports per model, by whole UTC day");
            }
        }

        if (provider is not (ProviderCatalog.OpenAi or ProviderCatalog.Cartesia))
        {
            return Unavailable(Units.Count, "this provider has no models");
        }

        var calls = await ByCallsAsync(provider, from, to, c => c.Model == "-" ? "unknown" : c.Model, ct);
        return calls with { Note = "calls per model (the model named in each request)" + (provider == ProviderCatalog.Cartesia ? "; Cartesia's per-model credits are not synced" : "") };
    }

    private async Task<Slices> ByCallsAsync(string provider, DateTime from, DateTime to, Func<ProviderCallStat, string> key, CancellationToken ct)
    {
        if (provider is not (ProviderCatalog.OpenAi or ProviderCatalog.Cartesia))
        {
            return Unavailable(Units.Count, "WarpTalk does not record calls to this provider");
        }

        var calls = await _unitOfWork.ProviderCallStats.GetRangeAsync(provider, from, to, ct);
        if (calls.Count == 0 && await _unitOfWork.ProviderCallStats.GetFirstHourAsync(provider, ct) is null)
        {
            return Unavailable(Units.Count, "no call to this provider has been recorded yet");
        }

        var items = calls.GroupBy(key)
            .Select(g => new AdminProviderBreakdownItemDto(
                g.Key, g.Key, g.Sum(Domain.Services.ProviderCallStatMerge.Calls), null, null, null,
                g.Sum(Domain.Services.ProviderCallStatMerge.Calls), g.Sum(Domain.Services.ProviderCallStatMerge.Failures)))
            .ToList();
        return new Slices(Units.Count, true, items, null);
    }

    private async Task<Slices> ByErrorClassAsync(string provider, DateTime from, DateTime to, CancellationToken ct)
    {
        var calls = await ByCallsAsync(provider, from, to, c => c.Operation, ct);
        if (!calls.Available) return calls;

        var rows = await _unitOfWork.ProviderCallStats.GetRangeAsync(provider, from, to, ct);
        long Sum(Func<ProviderCallStat, long> pick) => rows.Sum(pick);
        var counts = new (string Key, long Count)[]
        {
            ("quota", Sum(r => r.Quota)),
            ("rate_limited", Sum(r => r.RateLimited)),
            ("auth", Sum(r => r.Auth)),
            ("server_error", Sum(r => r.ServerError)),
            ("timeout", Sum(r => r.Timeout)),
            ("network_error", Sum(r => r.NetworkError)),
            ("error", Sum(r => r.Error)),
            ("client_error", Sum(r => r.ClientError)),
        };
        var items = counts.Select(c => new AdminProviderBreakdownItemDto(c.Key, c.Key, c.Count, null, null, null, c.Count, null)).ToList();
        var note = counts.All(c => c.Count == 0) ? "no failed call in the period" : "client_error is a request WarpTalk got wrong and does not count against the provider";
        return new Slices(Units.Count, true, items, note);
    }

    // ── uptime ────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<AdminProviderUptimeDto>> GetUptimeAsync(string provider, int? days, string? timeZoneId, CancellationToken ct = default)
    {
        var info = ProviderCatalog.Find(provider);
        if (info is null) return Result.Failure<AdminProviderUptimeDto>($"Unknown provider '{provider}'.", ErrorCodes.NotFound);
        var count = days ?? UptimeDays;
        if (count is < 1 or > UptimeDays)
        {
            return Result.Failure<AdminProviderUptimeDto>($"days must be between 1 and {UptimeDays}.", ErrorCodes.ValidationError);
        }

        if (!AdminComparisonRange.TryResolveTimeZone(timeZoneId, out var timeZone, out var error))
        {
            return Result.Failure<AdminProviderUptimeDto>(error!, ErrorCodes.ValidationError);
        }

        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var localDays = LastDays(now, timeZone, count);
            var from = localDays[0].Start;
            var to = localDays[^1].End;
            var shared = await ReadSharedAsync(from, to, ct);
            var input = await ReadProviderAsync(info.Key, from, to, now, shared, ct);
            var atoms = Atoms(input, from, to);
            var incidents = await _unitOfWork.ProviderStatusIncidents.GetOverlappingAsync(info.Key, from, to, ct);
            var coveredFrom = await StatusPageCoveredFromAsync(info.Key, ct);
            var uptime = Uptime(input, atoms, localDays, incidents, coveredFrom);

            string? note = null;
            if (input.CallsTrackedSince is { } since && since > from)
            {
                note = "our calls are tracked since " + TimeZoneInfo.ConvertTimeFromUtc(since, timeZone).ToString("yyyy-MM-dd", Invariant)
                    + "; earlier days show only the provider's own status page";
            }
            else if (input.CallsTrackedSince is null)
            {
                note = info.Key is ProviderCatalog.OpenAi or ProviderCatalog.Cartesia
                    ? "no call to this provider has been recorded yet; days show only the provider's own status page"
                    : "WarpTalk does not record its calls to this provider; days show only the provider's own status page";
            }

            return Result.Success(new AdminProviderUptimeDto(
                info.Key,
                timeZone.Id,
                uptime.Days,
                uptime.Percent,
                uptime.Basis,
                input.CallsTrackedSince,
                await StatusPageAsync(info.Key, ct),
                incidents.Take(20).Select(ToDto).ToList(),
                note));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin provider uptime failed. Provider: {Provider}", info.Key);
            return Result.Failure<AdminProviderUptimeDto>("An unexpected error occurred while reading the provider uptime.", ErrorCodes.InternalServerError);
        }
    }

    // ── reads ─────────────────────────────────────────────────────────────────────────────────

    private sealed record SharedReads(
        FxRateTable Fx,
        IReadOnlyList<ConsumptionSlotRow> Slots,
        IReadOnlyList<ProviderCallStat> Calls);

    private async Task<SharedReads> ReadSharedAsync(DateTime from, DateTime to, CancellationToken ct)
        => new(
            await ReadFxTableAsync(ct),
            await _unitOfWork.CreditTransactionRepository.GetConsumptionSlotsAsync(from, to, ct),
            await _unitOfWork.ProviderCallStats.GetRangeAsync(null, ProviderMetricsCalculator.HourOf(from), to, ct));

    private async Task<ProviderInputs> ReadProviderAsync(string provider, DateTime from, DateTime to, DateTime now, SharedReads shared, CancellationToken ct)
    {
        IReadOnlyList<CartesiaUsageDay> cartesia = [];
        decimal usdPerCredit = ProviderUsageConstants.DefaultCartesiaUsdPerCredit;
        IReadOnlyList<MediaUsageHourRow>? media = null;
        string? mediaError = null;
        decimal? livekitPrice = null;
        IReadOnlyList<ProviderPaymentRow> payments = [];
        DateTime? trackedSince = null;

        switch (provider)
        {
            case ProviderCatalog.Cartesia:
                cartesia = await ReadCartesiaDaysAsync(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to.AddTicks(-1) < now ? to.AddTicks(-1) : now), ct);
                var price = await _pricingConfig.ReadPricingConfigValueAsync(
                    ProviderUsageConstants.CartesiaUsdPerCreditConfigKey, ProviderUsageConstants.DefaultCartesiaUsdPerCredit, ct);
                usdPerCredit = price >= 0 ? price : ProviderUsageConstants.DefaultCartesiaUsdPerCredit;
                break;
            case ProviderCatalog.LiveKit:
                livekitPrice = await LiveKitPriceAsync(ct);
                if (_media is null)
                {
                    mediaError = "no translation-room client is configured";
                }
                else
                {
                    var read = await _media.GetHourlyAsync(from, to < now.AddHours(1) ? to : now.AddHours(1), ct);
                    if (read.IsSuccess) media = read.Value;
                    else mediaError = read.Error;
                }

                break;
            case ProviderCatalog.Stripe:
                payments = await _unitOfWork.PaymentRepository.GetProviderPaymentsAsync(ProviderCatalog.Stripe, from, to, ct);
                break;
        }

        if (provider is ProviderCatalog.OpenAi or ProviderCatalog.Cartesia)
        {
            trackedSince = await _unitOfWork.ProviderCallStats.GetFirstHourAsync(provider, ct) is { } first
                ? DateTime.SpecifyKind(first, DateTimeKind.Utc)
                : null;
        }

        return new ProviderInputs(
            provider, now, shared.Fx, shared.Slots, cartesia, usdPerCredit,
            shared.Calls, trackedSince, media, mediaError, livekitPrice, payments);
    }

    private async Task<decimal?> LiveKitPriceAsync(CancellationToken ct)
    {
        var price = await _pricingConfig.ReadPricingConfigValueAsync(LiveKitUsdPerParticipantMinuteKey, -1m, ct);
        return price >= 0 ? price : null;
    }

    /// <summary>Synced Cartesia days (totals only: this page reports every credit, not just dubbing's).</summary>
    private async Task<IReadOnlyList<CartesiaUsageDay>> ReadCartesiaDaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) return [];
        var totals = await _unitOfWork.ProviderUsageDaily.GetDaysAsync(
            ProviderUsageConstants.Providers.Cartesia, ProviderUsageConstants.GroupKinds.Total, from, to, ct);
        return totals.Select(r => new CartesiaUsageDay(r.UsageDate, r.Credits, 0, DateTime.SpecifyKind(r.SyncedAt, DateTimeKind.Utc))).ToList();
    }

    private async Task<FxRateTable> ReadFxTableAsync(CancellationToken ct)
    {
        if (_fx is not null) return await _fx.GetTableAsync(ct);
        var rows = await _unitOfWork.FxRates.GetPairAsync(FxRateConstants.Usd, FxRateConstants.Vnd, ct);
        var configured = await _pricingConfig.ReadPricingConfigValueAsync(AdminBillingInsightsCalculator.FxRateConfigKey, 0m, ct);
        return FxRateTable.From(rows, configured > 0 ? configured : null);
    }

    private async Task<DateTime?> StatusPageCoveredFromAsync(string provider, CancellationToken ct)
    {
        if (!_options.StatusPages.ContainsKey(provider)) return null;
        var oldest = await _unitOfWork.ProviderStatusIncidents.GetOldestStartedAtAsync(provider, ct);
        var snapshot = _statusPages is null ? null : await _statusPages.GetAsync(provider, ct);
        // No incident stored yet but the page was read: covered from that reading on.
        return oldest is { } started ? DateTime.SpecifyKind(started, DateTimeKind.Utc) : snapshot?.CheckedAt;
    }

    private async Task<AdminProviderStatusPageDto?> StatusPageAsync(string provider, CancellationToken ct)
    {
        if (!_options.StatusPages.TryGetValue(provider, out var url)) return null;
        var snapshot = _statusPages is null ? null : await _statusPages.GetAsync(provider, ct);
        return new AdminProviderStatusPageDto(
            url,
            snapshot?.Indicator,
            snapshot?.Description,
            snapshot?.CheckedAt,
            snapshot is null ? "not polled yet" : snapshot.Error);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0) return new Dictionary<Guid, string>();
        try
        {
            var result = await _workspaceClient.GetWorkspaceNamesAsync(distinct, ct);
            if (result.IsSuccess && result.Value is not null) return result.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin providers could not resolve workspace names.");
        }

        return new Dictionary<Guid, string>();
    }

    private static string NameOf(IReadOnlyDictionary<Guid, string> names, Guid id)
        => names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id.ToString()[..8];

    private static decimal? ToVnd(ProviderPaymentRow payment, FxRateTable fx)
    {
        var code = payment.Currency.Trim().ToUpperInvariant();
        if (code == FxRateConstants.Vnd) return payment.Total;
        if (code != FxRateConstants.Usd) return null;
        return fx.Resolve(payment.At).Rate is { } rate ? payment.Total * rate : null;
    }

    /// <summary>The last <paramref name="count"/> local days of <paramref name="timeZone"/>, today last.</summary>
    public static IReadOnlyList<(DateOnly Date, DateTime Start, DateTime End)> LastDays(DateTime now, TimeZoneInfo timeZone, int count)
    {
        var today = AdminComparisonRange.LocalDateOf(now, timeZone);
        return Enumerable.Range(0, count)
            .Select(offset => today.AddDays(offset - count + 1))
            .Select(date => (date, AdminComparisonRange.StartOfLocalDay(date, timeZone), AdminComparisonRange.StartOfLocalDay(date.AddDays(1), timeZone)))
            .ToList();
    }
}

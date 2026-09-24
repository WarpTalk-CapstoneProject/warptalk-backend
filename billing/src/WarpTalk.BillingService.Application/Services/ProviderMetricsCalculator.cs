using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Domain.Services;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>Everything one provider read needs, already fetched. Pure input: no clock beyond <see cref="Now"/>.</summary>
public sealed record ProviderInputs(
    string Provider,
    DateTime Now,
    FxRateTable Fx,
    IReadOnlyList<ConsumptionSlotRow> Slots,
    IReadOnlyList<CartesiaUsageDay> CartesiaDays,
    decimal CartesiaUsdPerCredit,
    IReadOnlyList<ProviderCallStat> Calls,
    DateTime? CallsTrackedSince,
    IReadOnlyList<MediaUsageHourRow>? Media,
    string? MediaError,
    decimal? LiveKitUsdPerParticipantMinute,
    IReadOnlyList<ProviderPaymentRow> Payments);

/// <summary>
/// The definitions behind the admin Providers page. Pure; tested on plain values.
///
/// EVERY SOURCE IS FIRST CUT INTO UTC HOURS, then summed into the requested buckets (local days of
/// the request's time zone, or hours), so a Vietnam evening lands on the right day.
///
/// USAGE, per provider (<see cref="ProviderCatalog.UsageUnits"/>):
///   OpenAI   — WarpTalk credits consumed by OpenAI-served charge types (the ledger). OpenAI's own
///              usage API needs an organisation admin key WarpTalk does not hold.
///   Cartesia — credits Cartesia itself reports (provider_usage_daily, synced by CartesiaUsageSyncWorker),
///              spread evenly over the hours of each UTC day because Cartesia's finest bucket is a day.
///              A day the sync does not cover is a gap, not a 0.
///   LiveKit  — estimated participant minutes from translation-room (see its MediaUsageCalculator).
///   Stripe   — payments that went through Stripe (paid and failed).
///
/// COST (USD, then VND at the USD→VND rate of each hour's UTC day, FxRateTable):
///   OpenAI   — Σ usage quantity × provider_unit_cost of the rate card each consume row settled on,
///              only where the card has one (TRANSLATION does not): the covered share is stated.
///   Cartesia — measured credits × cartesia_usd_per_credit on measured days; the rate-card estimate on
///              the rest. ALL Cartesia credits, including the non-dubbing ones Insights leaves out of
///              dubbing cost: this page is what Cartesia charged, not what dubbing cost.
///   LiveKit  — participant minutes × livekit_usd_per_participant_minute, only when that price is set.
///   Stripe   — unavailable: Stripe's fees are not synced into WarpTalk.
///
/// CALLS (OpenAI, Cartesia) — what the AI workers' own calls saw (provider_call_stats). A FAILURE is
/// quota (402), rate limit (429), auth (401/403), 5xx, timeout, network or an unclassified exception;
/// a client error (another 4xx) is a request we got wrong and does not count against the provider.
/// SUCCESS RATE ("live rate") = ok ÷ (ok + failures). Hours before the first recorded call are gaps.
///
/// STATUS of a day or of "now" is the worse of the calls signal and the provider's status page:
///   failure rate &lt; 2% operational · &lt; 10% degraded · &lt; 50% partial outage · else major outage
///   (under <see cref="MinCallsForRate"/> calls: any failure is degraded);
///   incident impact minor → degraded · major → partial outage · critical → major outage.
/// </summary>
public static class ProviderMetricsCalculator
{
    public const string Operational = "operational";
    public const string Degraded = "degraded";
    public const string PartialOutage = "partial_outage";
    public const string MajorOutage = "major_outage";
    public const string Unknown = "unknown";
    public const string NoData = "no_data";

    public const int MinCallsForRate = 5;

    public static class Granularities
    {
        public const string Day = "day";
        public const string Hour = "hour";
    }

    public static class Units
    {
        public const string Count = "count";
        public const string Credits = "credits";
        public const string ProviderCredits = "providerCredits";
        public const string Usd = "usd";
        public const string Vnd = "vnd";
        public const string Percent = "percent";
        public const string Ms = "ms";
        public const string Minutes = "minutes";
    }

    public static class Metrics
    {
        public const string Usage = "usage";
        public const string BilledCredits = "billedCredits";
        public const string CostUsd = "costUsd";
        public const string CostVnd = "costVnd";
        public const string Calls = "calls";
        public const string Failures = "failures";
        public const string ErrorRate = "errorRate";
        public const string P50 = "p50Ms";
        public const string P95 = "p95Ms";
        public const string RoomMinutes = "roomMinutes";
        public const string Recordings = "recordings";
        public const string FailedPayments = "failedPayments";
        public const string VolumeVnd = "volumeVnd";
    }

    /// <summary>The failure classes of provider_call_stats, in the order the page lists them.</summary>
    public static readonly IReadOnlyList<string> FailureClasses =
        ["quota", "rate_limited", "auth", "server_error", "timeout", "network_error", "error", "client_error"];

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ── hourly atoms ──────────────────────────────────────────────────────────────────────────

    /// <summary>One UTC hour of one provider. Each "Has…" flag says whether that source covered the hour.</summary>
    public sealed class Atom
    {
        public decimal LedgerCredits;
        public decimal LedgerCoveredCredits;
        public decimal LedgerCostUsd;
        public bool HasProviderCredits;
        public decimal ProviderCredits;
        public decimal ProviderCreditsCostUsd;
        public bool HasCalls;
        public long Ok;
        public long ClientErrors;
        public readonly Dictionary<string, long> FailuresByClass = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Latency = new(StringComparer.Ordinal);
        public bool HasMedia;
        public decimal ParticipantMinutes;
        public decimal RoomMinutes;
        public int Recordings;
        public int Payments;
        public int FailedPayments;
        public decimal VolumeVnd;
        public bool VolumeMissingRate;

        public long Failures => FailuresByClass.Values.Sum();
    }

    public static DateTime HourOf(DateTime at)
    {
        var utc = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Cuts every source of <paramref name="input"/> into UTC hours inside [from, to).</summary>
    public static IReadOnlyDictionary<DateTime, Atom> Atoms(ProviderInputs input, DateTime from, DateTime to)
    {
        var atoms = new Dictionary<DateTime, Atom>();
        Atom At(DateTime hour)
        {
            if (!atoms.TryGetValue(hour, out var atom)) atoms[hour] = atom = new Atom();
            return atom;
        }

        bool Inside(DateTime hour) => hour >= HourOf(from) && hour < to;

        foreach (var slot in input.Slots)
        {
            if (!string.Equals(slot.Provider, input.Provider, StringComparison.Ordinal)) continue;
            var hour = HourOf(slot.SlotStart);
            if (!Inside(hour)) continue;
            var atom = At(hour);
            atom.LedgerCredits += slot.Credits;
            atom.LedgerCoveredCredits += slot.CoveredCredits;
            atom.LedgerCostUsd += slot.CoveredCredits > 0 || slot.CoveredTransactions > 0 ? slot.CostUsd : 0m;
        }

        if (input.Provider == ProviderCatalog.Cartesia)
        {
            var today = DateOnly.FromDateTime(input.Now);
            foreach (var day in input.CartesiaDays.GroupBy(d => d.Date).Select(g => g.Last()))
            {
                var dayStart = day.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var dayEnd = dayStart.AddDays(1);
                // The P&L rule: a past day read before it closed is a partial number, not a measurement.
                if (day.SyncedAt < dayEnd && day.Date != today) continue;
                var dataEnd = day.SyncedAt < dayEnd ? HourOf(day.SyncedAt).AddHours(1) : dayEnd;
                var hours = Math.Max(1, (int)Math.Round((dataEnd - dayStart).TotalHours));
                var perHour = (decimal)day.TotalCredits / hours;
                for (var hour = dayStart; hour < dataEnd; hour = hour.AddHours(1))
                {
                    if (!Inside(hour)) continue;
                    var atom = At(hour);
                    atom.HasProviderCredits = true;
                    atom.ProviderCredits += perHour;
                    atom.ProviderCreditsCostUsd += perHour * input.CartesiaUsdPerCredit;
                }
            }
        }

        if (input.CallsTrackedSince is { } since)
        {
            for (var hour = HourOf(since > from ? since : from); hour < to && hour <= input.Now; hour = hour.AddHours(1))
            {
                At(hour).HasCalls = true;
            }
        }

        foreach (var call in input.Calls)
        {
            if (!string.Equals(call.Provider, input.Provider, StringComparison.Ordinal)) continue;
            var hour = HourOf(call.HourStart);
            if (!Inside(hour)) continue;
            var atom = At(hour);
            atom.HasCalls = true;
            atom.Ok += call.Ok;
            atom.ClientErrors += call.ClientError;
            AddFailure(atom, "quota", call.Quota);
            AddFailure(atom, "rate_limited", call.RateLimited);
            AddFailure(atom, "auth", call.Auth);
            AddFailure(atom, "server_error", call.ServerError);
            AddFailure(atom, "timeout", call.Timeout);
            AddFailure(atom, "network_error", call.NetworkError);
            AddFailure(atom, "error", call.Error);
            foreach (var (edge, count) in ProviderCallStatMerge.ParseBuckets(call.LatencyBuckets))
            {
                atom.Latency[edge] = atom.Latency.GetValueOrDefault(edge) + count;
            }
        }

        if (input.Media is not null)
        {
            for (var hour = HourOf(from); hour < to && hour <= input.Now; hour = hour.AddHours(1)) At(hour).HasMedia = true;
            foreach (var row in input.Media)
            {
                var hour = HourOf(row.HourStart);
                if (!Inside(hour)) continue;
                var atom = At(hour);
                atom.ParticipantMinutes += (decimal)row.ParticipantSeconds / 60m;
                atom.RoomMinutes += (decimal)row.RoomSeconds / 60m;
                atom.Recordings += row.Recordings;
            }
        }

        foreach (var payment in input.Payments)
        {
            var hour = HourOf(payment.At);
            if (!Inside(hour)) continue;
            var atom = At(hour);
            if (payment.Status == PaymentConstants.PaymentStatuses.Failed)
            {
                atom.FailedPayments++;
                continue;
            }

            atom.Payments++;
            var vnd = ToVnd(payment.Currency, payment.Total, payment.At, input.Fx);
            if (vnd is { } value) atom.VolumeVnd += value;
            else atom.VolumeMissingRate = true;
        }

        return atoms;
    }

    private static void AddFailure(Atom atom, string failureClass, long count)
    {
        if (count > 0) atom.FailuresByClass[failureClass] = atom.FailuresByClass.GetValueOrDefault(failureClass) + count;
    }

    private static decimal? ToVnd(string currency, decimal amount, DateTime at, FxRateTable fx)
    {
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (code == FxRateConstants.Vnd) return amount;
        if (code != FxRateConstants.Usd) return null;
        return fx.Resolve(at).Rate is { } rate ? amount * rate : null;
    }

    // ── one window ────────────────────────────────────────────────────────────────────────────

    /// <summary>The atoms of [start, end) summed, with each metric null when no hour in it was tracked.</summary>
    public sealed record WindowFigures(
        decimal? Usage,
        decimal? BilledCredits,
        decimal? CostUsd,
        decimal? CostVnd,
        long? Calls,
        long? Failures,
        long? ClientErrors,
        decimal? SuccessRate,
        decimal? ErrorRate,
        int? P50Ms,
        int? P95Ms,
        int? P99Ms,
        IReadOnlyDictionary<string, long> FailuresByClass,
        decimal? RoomMinutes,
        int? Recordings,
        int? FailedPayments,
        decimal? VolumeVnd,
        decimal LedgerCredits,
        decimal LedgerCoveredCredits,
        int MeasuredHours,
        int EstimatedHours);

    public static WindowFigures Window(ProviderInputs input, IReadOnlyDictionary<DateTime, Atom> atoms, DateTime start, DateTime end)
    {
        decimal ledgerCredits = 0, covered = 0, providerCredits = 0, costUsd = 0, costVnd = 0;
        bool anyProviderCredits = false, anyCalls = false, anyMedia = false, vndMissing = false, volumeMissing = false;
        long ok = 0, clientErrors = 0;
        var failures = new Dictionary<string, long>(StringComparer.Ordinal);
        var latency = new Dictionary<string, long>(StringComparer.Ordinal);
        decimal participantMinutes = 0, roomMinutes = 0, volume = 0;
        int recordings = 0, payments = 0, failedPayments = 0, measured = 0, estimated = 0;

        foreach (var (hour, atom) in atoms)
        {
            if (hour < HourOf(start) || hour >= end) continue;
            ledgerCredits += atom.LedgerCredits;
            covered += atom.LedgerCoveredCredits;

            decimal hourCost = 0;
            switch (input.Provider)
            {
                case ProviderCatalog.OpenAi:
                    hourCost = atom.LedgerCostUsd;
                    break;
                case ProviderCatalog.Cartesia:
                    if (atom.HasProviderCredits)
                    {
                        hourCost = atom.ProviderCreditsCostUsd;
                        if (atom.ProviderCredits > 0 || atom.LedgerCredits > 0) measured++;
                    }
                    else
                    {
                        hourCost = atom.LedgerCostUsd;
                        if (atom.LedgerCredits > 0) estimated++;
                    }

                    break;
                case ProviderCatalog.LiveKit:
                    hourCost = input.LiveKitUsdPerParticipantMinute is { } price ? atom.ParticipantMinutes * price : 0m;
                    break;
            }

            costUsd += hourCost;
            if (hourCost != 0)
            {
                if (input.Fx.Resolve(hour).Rate is { } rate) costVnd += hourCost * rate;
                else vndMissing = true;
            }

            if (atom.HasProviderCredits)
            {
                anyProviderCredits = true;
                providerCredits += atom.ProviderCredits;
            }

            if (atom.HasCalls)
            {
                anyCalls = true;
                ok += atom.Ok;
                clientErrors += atom.ClientErrors;
                foreach (var (failureClass, count) in atom.FailuresByClass) failures[failureClass] = failures.GetValueOrDefault(failureClass) + count;
                foreach (var (edge, count) in atom.Latency) latency[edge] = latency.GetValueOrDefault(edge) + count;
            }

            if (atom.HasMedia)
            {
                anyMedia = true;
                participantMinutes += atom.ParticipantMinutes;
                roomMinutes += atom.RoomMinutes;
                recordings += atom.Recordings;
            }

            payments += atom.Payments;
            failedPayments += atom.FailedPayments;
            volume += atom.VolumeVnd;
            volumeMissing |= atom.VolumeMissingRate;
        }

        var failed = failures.Values.Sum();
        var attributable = ok + failed;

        decimal? usage = input.Provider switch
        {
            ProviderCatalog.OpenAi => ledgerCredits,
            ProviderCatalog.Cartesia => anyProviderCredits ? Math.Round(providerCredits, 0) : null,
            ProviderCatalog.LiveKit => anyMedia ? Math.Round(participantMinutes, 1) : null,
            ProviderCatalog.Stripe => payments,
            _ => null,
        };

        decimal? cost = input.Provider switch
        {
            ProviderCatalog.OpenAi or ProviderCatalog.Cartesia => Math.Round(costUsd, 6),
            ProviderCatalog.LiveKit => anyMedia && input.LiveKitUsdPerParticipantMinute is not null ? Math.Round(costUsd, 6) : null,
            _ => null,
        };

        var hasCallMetrics = input.Provider is ProviderCatalog.OpenAi or ProviderCatalog.Cartesia;
        return new WindowFigures(
            usage,
            input.Provider is ProviderCatalog.OpenAi or ProviderCatalog.Cartesia ? ledgerCredits : null,
            cost,
            cost is null || vndMissing ? null : Math.Round(costVnd, 0),
            hasCallMetrics && anyCalls ? ok + failed + clientErrors : null,
            hasCallMetrics && anyCalls ? failed : null,
            hasCallMetrics && anyCalls ? clientErrors : null,
            hasCallMetrics && attributable > 0 ? Math.Round(ok * 100m / attributable, 2) : null,
            hasCallMetrics && attributable > 0 ? Math.Round(failed * 100m / attributable, 2) : null,
            Percentile(latency, 0.50),
            Percentile(latency, 0.95),
            Percentile(latency, 0.99),
            failures,
            input.Provider == ProviderCatalog.LiveKit && anyMedia ? Math.Round(roomMinutes, 1) : null,
            input.Provider == ProviderCatalog.LiveKit && anyMedia ? recordings : null,
            input.Provider == ProviderCatalog.Stripe ? failedPayments : null,
            input.Provider == ProviderCatalog.Stripe && !(volumeMissing && volume == 0) ? Math.Round(volume, 0) : null,
            ledgerCredits,
            covered,
            measured,
            estimated);
    }

    // ── latency ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <paramref name="quantile"/> of a (non-cumulative) histogram, interpolated inside its bucket
    /// as Prometheus's histogram_quantile does. A quantile in the +Inf bucket reports the last finite
    /// edge (a lower bound). Null for an empty histogram.
    /// </summary>
    public static int? Percentile(IReadOnlyDictionary<string, long> buckets, double quantile)
    {
        var ordered = buckets
            .Where(pair => pair.Value > 0)
            .Select(pair => (Edge: ProviderCallStatMerge.EdgeOf(pair.Key), Count: pair.Value))
            .OrderBy(pair => pair.Edge)
            .ToList();
        var total = ordered.Sum(pair => pair.Count);
        if (total == 0) return null;

        var rank = quantile * total;
        double lower = 0, cumulative = 0, lastFinite = 0;
        foreach (var (edge, count) in ordered)
        {
            if (double.IsPositiveInfinity(edge)) return (int)Math.Round(lastFinite);
            if (cumulative + count >= rank)
            {
                var within = count == 0 ? 0 : (rank - cumulative) / count;
                return (int)Math.Round(lower + (edge - lower) * within);
            }

            cumulative += count;
            lower = edge;
            lastFinite = edge;
        }

        return (int)Math.Round(lastFinite);
    }

    // ── status ────────────────────────────────────────────────────────────────────────────────

    public static int Rank(string? status) => status switch
    {
        Operational => 0,
        Degraded => 1,
        PartialOutage => 2,
        MajorOutage => 3,
        _ => -1,
    };

    public static string? Worst(string? a, string? b) => Rank(a) >= Rank(b) ? (Rank(a) < 0 ? b : a) : b;

    /// <summary>The status our own calls imply; null when there were none to judge by.</summary>
    public static string? StatusOfCalls(long ok, long failures)
    {
        var attributable = ok + failures;
        if (attributable == 0) return null;
        if (attributable < MinCallsForRate) return failures > 0 ? Degraded : Operational;
        var rate = (double)failures / attributable;
        return rate < 0.02 ? Operational : rate < 0.10 ? Degraded : rate < 0.50 ? PartialOutage : MajorOutage;
    }

    /// <summary>statuspage.io impact / indicator → status. none and maintenance are not an outage.</summary>
    public static string? StatusOfImpact(string? impact) => (impact ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "none" or "maintenance" => Operational,
        "minor" => Degraded,
        "major" => PartialOutage,
        "critical" => MajorOutage,
        _ => null,
    };

    // ── uptime ────────────────────────────────────────────────────────────────────────────────

    public sealed record UptimeResult(
        IReadOnlyList<AdminProviderUptimeDayDto> Days,
        decimal? Percent,
        string Basis);

    /// <summary>
    /// One status per local day, and the period's uptime. Uptime is, in order of preference:
    ///   calls      — ok ÷ (ok + failures) over every tracked day (what our calls actually saw);
    ///   statusPage — 100% minus the share of time a major or critical incident was open, from the
    ///                first day the stored incidents cover;
    ///   none       — no signal at all, and the percentage is null rather than a flattering 100.
    /// A day with no calls and no incident is operational only if the status page covered it;
    /// otherwise it is no_data.
    /// </summary>
    public static UptimeResult Uptime(
        ProviderInputs input,
        IReadOnlyDictionary<DateTime, Atom> atoms,
        IReadOnlyList<(DateOnly Date, DateTime Start, DateTime End)> days,
        IReadOnlyList<ProviderStatusIncident> incidents,
        DateTime? statusPageCoveredFrom)
    {
        var result = new List<AdminProviderUptimeDayDto>(days.Count);
        long ok = 0, failed = 0;

        foreach (var (date, start, end) in days)
        {
            var figures = Window(input, atoms, start, end);
            var tracked = input.CallsTrackedSince is { } since && since < end && start <= input.Now;
            var dayOk = (figures.Calls ?? 0) - (figures.Failures ?? 0) - (figures.ClientErrors ?? 0);
            var dayFailures = figures.Failures ?? 0;
            if (tracked)
            {
                ok += dayOk;
                failed += dayFailures;
            }

            var dayIncidents = incidents
                .Where(incident => incident.StartedAt < end && (incident.ResolvedAt ?? input.Now) > start)
                .OrderByDescending(incident => Rank(StatusOfImpact(incident.Impact)))
                .ThenBy(incident => incident.StartedAt)
                .ToList();

            var status = Worst(StatusOfCalls(dayOk, dayFailures), dayIncidents.Select(i => StatusOfImpact(i.Impact)).Aggregate((string?)null, Worst));
            if (status is null || Rank(status) < 0)
            {
                var pageCovered = statusPageCoveredFrom is { } covered && covered < end && start <= input.Now;
                status = pageCovered ? Operational : NoData;
            }

            if (start > input.Now) status = NoData;

            result.Add(new AdminProviderUptimeDayDto(
                date.ToString("yyyy-MM-dd", Invariant),
                status,
                tracked,
                figures.Calls ?? 0,
                dayFailures,
                figures.SuccessRate,
                figures.P95Ms,
                figures.FailuresByClass,
                dayIncidents.Select(ToDto).ToList()));
        }

        if (ok + failed > 0)
        {
            return new UptimeResult(result, Math.Round(ok * 100m / (ok + failed), 2), "calls");
        }

        if (statusPageCoveredFrom is { } coveredFrom && days.Count > 0)
        {
            var windowStart = days[0].Start > coveredFrom ? days[0].Start : coveredFrom;
            var windowEnd = days[^1].End < input.Now ? days[^1].End : input.Now;
            if (windowEnd > windowStart)
            {
                var down = MergedDowntime(incidents, windowStart, windowEnd, input.Now);
                var percent = 100m - (decimal)(down.TotalSeconds / (windowEnd - windowStart).TotalSeconds) * 100m;
                return new UptimeResult(result, Math.Round(Math.Clamp(percent, 0m, 100m), 2), "statusPage");
            }
        }

        return new UptimeResult(result, null, "none");
    }

    /// <summary>Time inside [from, to) covered by at least one major or critical incident (overlaps merged).</summary>
    public static TimeSpan MergedDowntime(IEnumerable<ProviderStatusIncident> incidents, DateTime from, DateTime to, DateTime now)
    {
        var spans = incidents
            .Where(i => Rank(StatusOfImpact(i.Impact)) >= Rank(PartialOutage))
            .Select(i => (Start: i.StartedAt < from ? from : i.StartedAt, End: Min(i.ResolvedAt ?? now, to)))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();

        var total = TimeSpan.Zero;
        DateTime? currentStart = null, currentEnd = null;
        foreach (var (start, end) in spans)
        {
            if (currentEnd is null || start > currentEnd)
            {
                if (currentStart is not null) total += currentEnd!.Value - currentStart.Value;
                currentStart = start;
                currentEnd = end;
            }
            else if (end > currentEnd)
            {
                currentEnd = end;
            }
        }

        if (currentStart is not null) total += currentEnd!.Value - currentStart.Value;
        return total;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    public static AdminProviderIncidentDto ToDto(ProviderStatusIncident incident)
        => new(incident.Name, incident.Impact, incident.Status,
            DateTime.SpecifyKind(incident.StartedAt, DateTimeKind.Utc),
            incident.ResolvedAt is { } resolved ? DateTime.SpecifyKind(resolved, DateTimeKind.Utc) : null,
            incident.Url);

    // ── series ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The metric keys and units a provider has, in the order the page offers them.</summary>
    public static IReadOnlyList<(string Key, string Unit)> MetricsOf(string provider) => provider switch
    {
        ProviderCatalog.OpenAi =>
        [
            (Metrics.Usage, Units.Credits), (Metrics.CostUsd, Units.Usd), (Metrics.CostVnd, Units.Vnd),
            (Metrics.Calls, Units.Count), (Metrics.Failures, Units.Count), (Metrics.ErrorRate, Units.Percent),
            (Metrics.P50, Units.Ms), (Metrics.P95, Units.Ms),
        ],
        ProviderCatalog.Cartesia =>
        [
            (Metrics.Usage, Units.ProviderCredits), (Metrics.BilledCredits, Units.Credits), (Metrics.CostUsd, Units.Usd),
            (Metrics.CostVnd, Units.Vnd), (Metrics.Calls, Units.Count), (Metrics.Failures, Units.Count),
            (Metrics.ErrorRate, Units.Percent), (Metrics.P50, Units.Ms), (Metrics.P95, Units.Ms),
        ],
        ProviderCatalog.LiveKit =>
        [
            (Metrics.Usage, Units.Minutes), (Metrics.RoomMinutes, Units.Minutes), (Metrics.Recordings, Units.Count),
            (Metrics.CostUsd, Units.Usd), (Metrics.CostVnd, Units.Vnd),
        ],
        ProviderCatalog.Stripe =>
        [
            (Metrics.Usage, Units.Count), (Metrics.FailedPayments, Units.Count), (Metrics.VolumeVnd, Units.Vnd),
            (Metrics.CostUsd, Units.Usd),
        ],
        _ => [],
    };

    public static decimal? ValueOf(WindowFigures figures, string metric) => metric switch
    {
        Metrics.Usage => figures.Usage,
        Metrics.BilledCredits => figures.BilledCredits,
        Metrics.CostUsd => figures.CostUsd,
        Metrics.CostVnd => figures.CostVnd,
        Metrics.Calls => figures.Calls,
        Metrics.Failures => figures.Failures,
        Metrics.ErrorRate => figures.ErrorRate,
        Metrics.P50 => figures.P50Ms,
        Metrics.P95 => figures.P95Ms,
        Metrics.RoomMinutes => figures.RoomMinutes,
        Metrics.Recordings => figures.Recordings,
        Metrics.FailedPayments => figures.FailedPayments,
        Metrics.VolumeVnd => figures.VolumeVnd,
        _ => null,
    };

    /// <summary>Why a metric is null or partial over a whole period, in words the page shows.</summary>
    public static string? NoteOf(ProviderInputs input, WindowFigures total, string metric)
    {
        switch (metric)
        {
            case Metrics.CostUsd or Metrics.CostVnd:
                switch (input.Provider)
                {
                    case ProviderCatalog.Stripe:
                        return "Stripe's processing fees are not synced into WarpTalk, so its cost is not known here";
                    case ProviderCatalog.LiveKit when input.LiveKitUsdPerParticipantMinute is null:
                        return "no LiveKit price is configured (billing_pricing_config livekit_usd_per_participant_minute)";
                    case ProviderCatalog.LiveKit when input.Media is null:
                        return "LiveKit usage is unavailable: " + (input.MediaError ?? "translation-room did not answer");
                    case ProviderCatalog.OpenAi when total.LedgerCredits > total.LedgerCoveredCredits:
                        return string.Create(Invariant,
                            $"covers {Coverage(total):0.#}% of OpenAI credits: only charge types whose rate card carries a provider price (TRANSLATION has none) are costed");
                    case ProviderCatalog.Cartesia when total.EstimatedHours > 0:
                        return string.Create(Invariant,
                            $"measured from Cartesia's usage API where synced; estimated from rate cards for {total.EstimatedHours} hour(s) it does not cover");
                }

                if (metric == Metrics.CostVnd && total.CostUsd is not null && total.CostVnd is null)
                {
                    return "no USD→VND rate is recorded or configured";
                }

                return null;
            case Metrics.Usage when input.Provider == ProviderCatalog.Cartesia:
                return total.Usage is null
                    ? "Cartesia's usage API has not been synced for this period (CARTESIA_ADMIN_API_KEY)"
                    : "credits Cartesia reports per UTC day (≈1 per character), spread evenly over the day's hours";
            case Metrics.Usage when input.Provider == ProviderCatalog.OpenAi:
                return "WarpTalk credits charged for OpenAI-served features; OpenAI's own token usage needs an org admin key WarpTalk does not hold";
            case Metrics.Usage or Metrics.RoomMinutes or Metrics.Recordings when input.Provider == ProviderCatalog.LiveKit:
                if (input.Media is null) return "LiveKit usage is unavailable: " + (input.MediaError ?? "translation-room did not answer");
                return metric == Metrics.Usage
                    ? "estimated from each participant's last join→leave; dubbing/ingress bots and rejoins are not counted, so LiveKit's invoice will be higher"
                    : null;
            case Metrics.Calls or Metrics.Failures or Metrics.ErrorRate or Metrics.P50 or Metrics.P95:
                if (input.CallsTrackedSince is null) return "no call to this provider has been recorded yet (AI workers record them since warptalk-ai provider_calls)";
                if (metric is Metrics.P50 or Metrics.P95 && input.Provider == ProviderCatalog.Cartesia)
                    return "dubbing latency is time to first audio over the websocket";
                return null;
            case Metrics.VolumeVnd when total.VolumeVnd is null:
                return "payments in a currency with no rate to VND";
            default:
                return null;
        }
    }

    public static decimal Coverage(WindowFigures figures)
        => figures.LedgerCredits <= 0 ? 100m : Math.Round(figures.LedgerCoveredCredits * 100m / figures.LedgerCredits, 1);
}

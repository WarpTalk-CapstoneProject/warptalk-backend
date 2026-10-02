using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

/// <summary>Everything one profit-and-loss read needs, already fetched. Rows may span more than one window.</summary>
public sealed record ProfitAndLossInputs(
    IReadOnlyList<ConsumptionSlotRow> Slots,
    IReadOnlyList<WorkspaceSlotRow> WorkspaceSlots,
    IReadOnlyList<PaidAmountRow> Payments,
    IReadOnlyList<CartesiaUsageDay> CartesiaDays,
    decimal CartesiaUsdPerCredit,
    FxRateTable Fx,
    DateTime Now);

/// <summary>A provider inside one window.</summary>
public sealed record ProviderFigures(
    string Provider,
    long Credits,
    long CoveredCredits,
    decimal CostUsd,
    decimal MeasuredUsd,
    IReadOnlyList<(string ChargeType, long Credits, long CoveredCredits, decimal CostUsd)> Services);

/// <summary>A plan inside one window. <see cref="PlanId"/> null = not attributable to a plan.</summary>
public sealed record PlanFigures(
    Guid? PlanId,
    decimal? RevenueUsd,
    int Payments,
    long Credits,
    long CoveredCredits,
    decimal CostUsd,
    int ActiveWorkspaces);

/// <summary>One window's profit and loss.</summary>
public sealed record PeriodPnl(
    AdminBillingInsightsCalculator.MetricSide Revenue,
    AdminBillingInsightsCalculator.MetricSide AiCost,
    decimal AiCostUsd,
    long Credits,
    long CoveredCredits,
    decimal CoveragePercent,
    int ActiveWorkspaces,
    IReadOnlyList<ChargeTypeCoverage> ByChargeType,
    int MeasuredDays,
    int EstimatedDays,
    IReadOnlyList<ProviderFigures> Providers,
    IReadOnlyList<PlanFigures> Plans,
    IReadOnlyList<FxRateResolution> FxUsed)
{
    public AdminBillingInsightsCalculator.MetricSide GrossMargin => ProfitAndLossCalculator.GrossMargin(Revenue, AiCost, CoveragePercent, ByChargeType, Credits);

    public AdminBillingInsightsCalculator.MetricSide GrossMarginPercent => ProfitAndLossCalculator.MarginPercent(Revenue, GrossMargin);

    public AdminBillingInsightsCalculator.MetricSide Arpa => ProfitAndLossCalculator.Arpa(Revenue, ActiveWorkspaces);
}

/// <summary>
/// Profit and loss for the admin Insights page: revenue, AI provider cost, gross margin, margin %, ARPA,
/// per provider and per plan, over any window. Pure: no clock, no I/O; tested on plain values.
///
/// Everything is in USD, the accounting currency.
///
/// REVENUE is the counted paid payments (see IPaymentRepository), USD as is, VND at the USD→VND rate of
/// the UTC day it was paid, any other currency excluded and named.
///
/// AI PROVIDER COST follows the rules of the Insights aiProviderCost metric, at half-hour grain:
///   - a consume row settled on a rate card with a provider_unit_cost costs quantity × that cost (USD);
///   - on a UTC day the Cartesia usage sync covers (closed and read after it closed, or today), the
///     Cartesia-served charge types drop their rate-card estimate: that day costs the measured Cartesia
///     dubbing credits × cartesia_usd_per_credit, spread evenly over the part of the day the data covers
///     (the same pro-rating CartesiaDubbingCost applies at a period's edges), and its dubbing rows count
///     as covered;
///   - anything else (TRANSLATION has no provider price) is uncovered: left out of the cost, and the
///     share it represents is stated, so the margin is flagged as overstated;
///   - providers bill in USD, so no cost is converted.
///
/// ACTIVE WORKSPACES are those that paid OR consumed credits in the window; ARPA = revenue ÷ them.
/// </summary>
public static class ProfitAndLossCalculator
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string Vnd = FxRateConstants.Vnd;
    private const string Usd = FxRateConstants.Usd;

    public static PeriodPnl Compute(ProfitAndLossInputs input, DateTime from, DateTime to)
    {
        var fxUsed = new List<FxRateResolution>();

        // ── Revenue ──
        decimal revenue = 0;
        int included = 0, excluded = 0;
        var excludedCurrencies = new SortedSet<string>(StringComparer.Ordinal);
        var revenueByPlan = new Dictionary<Guid, (decimal Amount, int Included, int Excluded)>();
        var activeByPlan = new Dictionary<Guid, HashSet<Guid>>();
        var active = new HashSet<Guid>();

        foreach (var payment in input.Payments)
        {
            if (payment.At < from || payment.At >= to) continue;
            if (payment.WorkspaceId is { } paidBy)
            {
                active.Add(paidBy);
                ActiveSet(activeByPlan, payment.PlanId).Add(paidBy);
            }

            var amount = ToUsd(payment.Currency, payment.Total, payment.At, input.Fx, fxUsed);
            var plan = revenueByPlan.GetValueOrDefault(Key(payment.PlanId));
            if (amount is { } usd)
            {
                revenue += usd;
                included++;
                revenueByPlan[Key(payment.PlanId)] = (plan.Amount + usd, plan.Included + 1, plan.Excluded);
            }
            else
            {
                excluded++;
                excludedCurrencies.Add(string.IsNullOrWhiteSpace(payment.Currency) ? "unknown-currency" : payment.Currency.Trim().ToUpperInvariant());
                revenueByPlan[Key(payment.PlanId)] = (plan.Amount, plan.Included, plan.Excluded + 1);
            }
        }

        var revenueSide = included == 0 && excluded > 0
            ? AdminBillingInsightsCalculator.MetricSide.Unavailable(
                $"every payment was in a currency that cannot be converted ({string.Join("/", excludedCurrencies)})")
            : new AdminBillingInsightsCalculator.MetricSide(
                AdminBillingInsightsCalculator.RoundUsd(revenue),
                excluded == 0 ? null : string.Create(Invariant, $"excludes {excluded} {string.Join("/", excludedCurrencies)} payment(s) with no rate to USD"));

        foreach (var row in input.WorkspaceSlots)
        {
            if (row.SlotStart < from || row.SlotStart >= to || row.Credits <= 0) continue;
            active.Add(row.WorkspaceId);
            ActiveSet(activeByPlan, row.PlanId).Add(row.WorkspaceId);
        }

        // ── Cost ──
        var measured = MeasuredDays(input, from, to);
        var cost = new CostAccumulator();
        // Cartesia-served credits per (UTC day, plan) on measured days: the key the measured cost is allocated by.
        var measuredShares = new Dictionary<DateOnly, Dictionary<Guid, long>>();

        foreach (var row in input.Slots)
        {
            if (row.SlotStart < from || row.SlotStart >= to) continue;
            var day = DateOnly.FromDateTime(row.SlotStart);
            var isMeasured = measured.ContainsKey(day) && ProviderUsageConstants.CartesiaChargeTypes.Contains(row.ChargeType);

            decimal usd = 0;
            long covered;
            if (isMeasured)
            {
                // The rate-card estimate gives way to measured Cartesia credits; the credits are covered.
                covered = row.Credits;
                var shares = measuredShares.TryGetValue(day, out var existing) ? existing : measuredShares[day] = new();
                shares[Key(row.PlanId)] = shares.GetValueOrDefault(Key(row.PlanId)) + row.Credits;
            }
            else
            {
                covered = row.CoveredCredits;
                usd = row.CoveredCredits > 0 || row.CoveredTransactions > 0 ? row.CostUsd : 0m;
            }

            cost.Add(row.Provider, row.ChargeType, row.PlanId, row.Credits, covered, usd);
        }

        foreach (var (day, share) in measured)
        {
            if (share.Usd <= 0) continue;
            var byPlan = measuredShares.GetValueOrDefault(day);
            var total = byPlan?.Values.Sum() ?? 0;
            if (byPlan is null || total <= 0)
            {
                cost.AddMeasured(AiProviderCatalog.Cartesia, null, share.Usd);
                continue;
            }

            foreach (var (plan, credits) in byPlan)
            {
                cost.AddMeasured(AiProviderCatalog.Cartesia, Unkey(plan), share.Usd * credits / total);
            }
        }

        var coverage = cost.Credits <= 0 ? 100m : Math.Round(cost.CoveredCredits * 100m / cost.Credits, 1, MidpointRounding.AwayFromZero);
        var byType = cost.ByChargeType();
        var aiCost = AiCostSide(cost, byType, coverage, measured.Count);

        var planIds = revenueByPlan.Keys.Concat(cost.PlanIds).Concat(activeByPlan.Keys).Distinct().ToList();
        var plans = planIds
            .Select(plan =>
            {
                var rev = revenueByPlan.GetValueOrDefault(plan);
                var planCost = cost.Plan(Unkey(plan));
                return new PlanFigures(
                    Unkey(plan),
                    rev.Included == 0 && rev.Excluded > 0 ? null : AdminBillingInsightsCalculator.RoundUsd(rev.Amount),
                    rev.Included + rev.Excluded,
                    planCost.Credits,
                    planCost.CoveredCredits,
                    Math.Round(planCost.Usd, 6, MidpointRounding.AwayFromZero),
                    activeByPlan.TryGetValue(plan, out var set) ? set.Count : 0);
            })
            .ToList();

        return new PeriodPnl(
            revenueSide,
            aiCost,
            Math.Round(cost.Usd, 6, MidpointRounding.AwayFromZero),
            cost.Credits,
            cost.CoveredCredits,
            coverage,
            active.Count,
            byType,
            measured.Count,
            CartesiaDubbingCost.UtcDaysOf(from, to, input.Now).Count() - measured.Count,
            cost.Providers(),
            plans,
            fxUsed);
    }

    // ── metric rules ────────────────────────────────────────────────────────

    private static AdminBillingInsightsCalculator.MetricSide AiCostSide(
        CostAccumulator cost, IReadOnlyList<ChargeTypeCoverage> byType, decimal coverage, int measuredDays)
    {
        var totals = new ConsumptionTotals(cost.Credits, 0, 0, cost.CoveredCredits, 0, cost.Usd, byType);
        var uncovered = AdminBillingInsightsCalculator.UncoveredChargeTypes(totals);

        if (cost.Credits == 0 && cost.MeasuredUsd == 0) return new(0m, null);
        if (cost.CoveredCredits == 0 && cost.MeasuredUsd == 0)
        {
            return AdminBillingInsightsCalculator.MetricSide.Unavailable(
                "cannot be reconstructed: no consumed credit was settled on a rate card with a provider cost"
                + (uncovered is null ? string.Empty : $" ({uncovered})"));
        }

        var notes = new List<string>();
        if (coverage < 100m)
        {
            notes.Add(string.Create(Invariant, $"covers {coverage:0.#}% of consumed credits"));
            if (uncovered is not null) notes.Add(uncovered);
        }

        if (measuredDays > 0)
        {
            notes.Add(string.Create(Invariant, $"dubbing measured from Cartesia usage on {measuredDays} UTC day{(measuredDays == 1 ? "" : "s")}, estimated from rate cards on the rest"));
        }

        return new(AdminBillingInsightsCalculator.RoundUsd(cost.Usd), notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>revenue − AI cost; flagged as overstated when the cost does not cover every credit.</summary>
    public static AdminBillingInsightsCalculator.MetricSide GrossMargin(
        AdminBillingInsightsCalculator.MetricSide revenue,
        AdminBillingInsightsCalculator.MetricSide aiCost,
        decimal coveragePercent,
        IReadOnlyList<ChargeTypeCoverage> byType,
        long credits)
    {
        if (revenue.Value is null) return AdminBillingInsightsCalculator.MetricSide.Unavailable("revenue is unavailable");
        if (aiCost.Value is null) return AdminBillingInsightsCalculator.MetricSide.Unavailable("AI provider cost is unavailable");
        string? note = null;
        if (coveragePercent < 100m)
        {
            var uncovered = AdminBillingInsightsCalculator.UncoveredChargeTypes(new ConsumptionTotals(credits, 0, 0, 0, 0, 0, byType));
            note = string.Create(Invariant, $"AI cost covers only {coveragePercent:0.#}% of consumed credits")
                   + (uncovered is null ? string.Empty : $" ({uncovered})")
                   + ", so this margin is overstated";
        }

        return new(revenue.Value.Value - aiCost.Value.Value, note);
    }

    /// <summary>margin ÷ revenue × 100, one decimal; null when there is no revenue to divide by.</summary>
    public static AdminBillingInsightsCalculator.MetricSide MarginPercent(
        AdminBillingInsightsCalculator.MetricSide revenue, AdminBillingInsightsCalculator.MetricSide margin)
    {
        if (revenue.Value is null || margin.Value is null) return AdminBillingInsightsCalculator.MetricSide.Unavailable("gross margin is unavailable");
        if (revenue.Value.Value == 0) return AdminBillingInsightsCalculator.MetricSide.Unavailable("no revenue in the period");
        return new(Math.Round(margin.Value.Value * 100m / revenue.Value.Value, 1, MidpointRounding.AwayFromZero), margin.Note);
    }

    /// <summary>ARPA = revenue ÷ workspaces that paid or consumed credits in the period.</summary>
    public static AdminBillingInsightsCalculator.MetricSide Arpa(AdminBillingInsightsCalculator.MetricSide revenue, int activeWorkspaces)
    {
        if (revenue.Value is null) return AdminBillingInsightsCalculator.MetricSide.Unavailable("revenue is unavailable");
        if (activeWorkspaces == 0) return AdminBillingInsightsCalculator.MetricSide.Unavailable("no workspace paid or used credits in the period");
        return new(
            AdminBillingInsightsCalculator.RoundUsd(revenue.Value.Value / activeWorkspaces),
            string.Create(Invariant, $"revenue ÷ {activeWorkspaces} workspace{(activeWorkspaces == 1 ? "" : "s")} that paid or used credits"));
    }

    // ── measured Cartesia days ──────────────────────────────────────────────

    /// <summary>
    /// The UTC days of [from, to) the Cartesia sync covers, each with the measured dubbing cost that falls
    /// inside the window — the same day rule and edge pro-rating as <see cref="CartesiaDubbingCost.Measure"/>.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, (decimal Credits, decimal Usd)> MeasuredDays(ProfitAndLossInputs input, DateTime from, DateTime to)
    {
        var result = new Dictionary<DateOnly, (decimal, decimal)>();
        if (input.CartesiaDays.Count == 0) return result;

        var synced = input.CartesiaDays.GroupBy(day => day.Date).ToDictionary(group => group.Key, group => group.Last());
        var today = DateOnly.FromDateTime(input.Now);
        foreach (var date in CartesiaDubbingCost.UtcDaysOf(from, to, input.Now))
        {
            var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);
            if (!synced.TryGetValue(date, out var day) || (day.SyncedAt < dayEnd && date != today)) continue;

            var dataEnd = day.SyncedAt < dayEnd ? day.SyncedAt : dayEnd;
            var windowEnd = to < dataEnd ? to : dataEnd;
            var windowStart = from > dayStart ? from : dayStart;
            var overlap = windowEnd - windowStart;
            var span = dataEnd - dayStart;
            var fraction = span <= TimeSpan.Zero || overlap <= TimeSpan.Zero ? 0m : Math.Min(1m, (decimal)overlap.Ticks / span.Ticks);
            var credits = day.DubbingCredits * fraction;
            result[date] = (credits, credits * input.CartesiaUsdPerCredit);
        }

        return result;
    }

    // ── currency ────────────────────────────────────────────────────────────

    private static decimal? ToUsd(string currency, decimal amount, DateTime at, FxRateTable fx, List<FxRateResolution> used)
    {
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (code == Usd) return amount;
        if (code != Vnd) return null;
        var rate = fx.Resolve(at);
        if (rate.Rate is not { } value || value <= 0) return null;
        used.Add(rate);
        return amount / value;
    }

    private static HashSet<Guid> ActiveSet(Dictionary<Guid, HashSet<Guid>> byPlan, Guid? plan)
        => byPlan.TryGetValue(Key(plan), out var set) ? set : byPlan[Key(plan)] = new HashSet<Guid>();

    /// <summary>Dictionaries cannot key on a null: no plan is <see cref="Guid.Empty"/> inside, null outside.</summary>
    private static Guid Key(Guid? plan) => plan ?? Guid.Empty;

    private static Guid? Unkey(Guid plan) => plan == Guid.Empty ? null : plan;

    /// <summary>Sums of one window, by provider, charge type and plan.</summary>
    private sealed class CostAccumulator
    {
        private readonly Dictionary<string, ProviderSum> _providers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Credits, long Covered)> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, PlanSum> _plans = new();

        public long Credits { get; private set; }
        public long CoveredCredits { get; private set; }
        public decimal Usd { get; private set; }
        public decimal MeasuredUsd { get; private set; }

        public IEnumerable<Guid> PlanIds => _plans.Keys;

        public void Add(string provider, string chargeType, Guid? plan, long credits, long covered, decimal usd)
        {
            Credits += credits;
            CoveredCredits += covered;
            Usd += usd;

            var p = Provider(provider);
            p.Credits += credits;
            p.Covered += covered;
            p.Usd += usd;
            var service = p.Services.GetValueOrDefault(chargeType);
            p.Services[chargeType] = (service.Credits + credits, service.Covered + covered, service.Usd + usd);

            var type = _types.GetValueOrDefault(chargeType);
            _types[chargeType] = (type.Credits + credits, type.Covered + covered);

            var planSum = Plan(plan);
            planSum.Credits += credits;
            planSum.CoveredCredits += covered;
            planSum.Usd += usd;
        }

        public void AddMeasured(string provider, Guid? plan, decimal usd)
        {
            Usd += usd;
            MeasuredUsd += usd;

            var p = Provider(provider);
            p.Usd += usd;
            p.Measured += usd;

            var planSum = Plan(plan);
            planSum.Usd += usd;
            planSum.Measured += usd;
        }

        public PlanSum Plan(Guid? plan) => _plans.TryGetValue(Key(plan), out var sum) ? sum : _plans[Key(plan)] = new PlanSum();

        private ProviderSum Provider(string provider) => _providers.TryGetValue(provider, out var sum) ? sum : _providers[provider] = new ProviderSum();

        public IReadOnlyList<ChargeTypeCoverage> ByChargeType()
            => _types
                .Select(pair => new ChargeTypeCoverage(pair.Key, pair.Value.Credits, pair.Value.Covered))
                .OrderByDescending(type => type.Credits)
                .ThenBy(type => type.ChargeType, StringComparer.Ordinal)
                .ToList();

        public IReadOnlyList<ProviderFigures> Providers()
            => _providers
                .Select(pair => new ProviderFigures(
                    pair.Key,
                    pair.Value.Credits,
                    pair.Value.Covered,
                    Math.Round(pair.Value.Usd, 6, MidpointRounding.AwayFromZero),
                    Math.Round(pair.Value.Measured, 6, MidpointRounding.AwayFromZero),
                    pair.Value.Services
                        .Select(service => (service.Key, service.Value.Credits, service.Value.Covered, Math.Round(service.Value.Usd, 6, MidpointRounding.AwayFromZero)))
                        .OrderByDescending(service => service.Credits)
                        .ToList()))
                .OrderByDescending(provider => provider.CostUsd)
                .ThenByDescending(provider => provider.Credits)
                .ThenBy(provider => provider.Provider, StringComparer.Ordinal)
                .ToList();

        private sealed class ProviderSum
        {
            public long Credits;
            public long Covered;
            public decimal Usd;
            public decimal Measured;
            public readonly Dictionary<string, (long Credits, long Covered, decimal Usd)> Services = new(StringComparer.Ordinal);
        }
    }

    public sealed class PlanSum
    {
        public long Credits;
        public long CoveredCredits;
        public decimal Usd;
        public decimal Measured;
    }
}

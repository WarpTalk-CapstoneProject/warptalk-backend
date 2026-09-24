using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.DTOs;

/// <summary>
/// <c>GET ~/api/v1/admin/billing/fx</c>: the USD→VND rate reports use today, where it came from, and
/// whether it is fresh.
///
/// <paramref name="Rate"/> is today's effective rate (null only when no rate exists anywhere).
/// <paramref name="Source"/>: <c>stripe_fx_quote</c> | <c>stripe_charge</c> | <c>manual</c> | <c>configured</c>.
/// <paramref name="AsOf"/> is when that rate was fetched or set. <paramref name="Mode"/> is <c>stripe</c>
/// (default) or <c>manual</c>. <paramref name="Stale"/> is true when, in Stripe mode, Stripe has not given a
/// rate for more than a day — reports then run on the last known rate and <paramref name="Warning"/>
/// says so. <paramref name="History"/> is the effective rate of each recorded UTC day, newest last.
/// </summary>
public sealed record AdminFxRateStatusDto(
    string BaseCurrency,
    string QuoteCurrency,
    decimal? Rate,
    string Source,
    string SourceLabel,
    string? RateDate,
    DateTime? AsOf,
    string Basis,
    string Mode,
    decimal? ManualRate,
    AdminFxStripeRateDto? LatestStripe,
    bool Stale,
    string? Warning,
    IReadOnlyList<AdminFxRateDayDto> History);

/// <summary>The newest rate Stripe gave. <paramref name="FeeInclusiveRate"/> is after Stripe's FX fee (FX quotes only).</summary>
public sealed record AdminFxStripeRateDto(
    decimal Rate,
    decimal? FeeInclusiveRate,
    string Source,
    string RateDate,
    DateTime FetchedAt,
    string? SourceRef);

public sealed record AdminFxRateDayDto(string Date, decimal Rate, string Source);

/// <summary><c>POST ~/api/v1/admin/billing/fx/refresh</c>. <paramref name="Error"/> says why Stripe gave nothing; the status still answers.</summary>
public sealed record AdminFxRefreshResultDto(
    bool QuoteRecorded,
    int ChargeDaysRecorded,
    string? Error,
    AdminFxRateStatusDto Status);

/// <summary><c>PUT ~/api/v1/admin/billing/fx/override</c>: VND per US dollar, used instead of Stripe's from today.</summary>
public sealed record SetFxOverrideRequest(decimal Rate);

using System;
using System.Collections.Generic;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.DTOs;

// Admin Providers page (2026-09-25). Every figure is nullable on purpose: null means "not
// measured / not tracked", which the page says in words, never a 0.

/// <summary>GET /api/v1/admin/providers.</summary>
public sealed record AdminProvidersOverviewDto(
    DateTime GeneratedAt,
    string TimeZone,
    IReadOnlyList<AdminProviderSummaryDto> Providers);

/// <summary>
/// One provider card. <paramref name="Status"/>: operational | degraded | partial_outage | major_outage | unknown.
/// <paramref name="StatusSource"/>: calls | statusPage | both | none.
/// </summary>
public sealed record AdminProviderSummaryDto(
    string Key,
    string Name,
    string Category,
    IReadOnlyList<string> Services,
    string Status,
    string StatusSource,
    string? StatusNote,
    AdminProviderStatusPageDto? StatusPage,
    AdminProviderTodayDto Today,
    AdminProviderLiveDto Live,
    AdminProviderUptimeHeadlineDto Uptime,
    IReadOnlyList<AdminProviderConfigItemDto> Config);

/// <summary>Today on the request's local calendar. <paramref name="UsageUnit"/> is a ProviderCatalog.UsageUnits value.</summary>
public sealed record AdminProviderTodayDto(
    string UsageUnit,
    decimal? Usage,
    decimal? CostUsd,
    decimal? CostVnd,
    string? UsageNote,
    string? CostNote);

/// <summary>
/// Our calls over the last 24 hours. <paramref name="SuccessRate"/> ("live rate") = ok ÷ (ok + provider
/// failures); client errors (a request WE got wrong) count in <paramref name="Calls"/> but not against it.
/// </summary>
public sealed record AdminProviderLiveDto(
    long? Calls,
    long? Failures,
    decimal? SuccessRate,
    decimal? ErrorRate,
    int? P50Ms,
    int? P95Ms,
    long? CallsLastHour,
    string? Note);

/// <summary><paramref name="Basis"/>: calls | statusPage | none. <paramref name="TrackedSince"/>: first hour of recorded calls.</summary>
public sealed record AdminProviderUptimeHeadlineDto(
    decimal? Percent,
    string Basis,
    DateTime? TrackedSince,
    int Days);

public sealed record AdminProviderStatusPageDto(
    string Url,
    string? Indicator,
    string? Description,
    DateTime? CheckedAt,
    string? Error);

/// <summary><paramref name="State"/>: yes | no | info. Never a secret: whether something is set, not what it is.</summary>
public sealed record AdminProviderConfigItemDto(string Key, string Label, string Value, string State);

/// <summary>A bucket of the series: a local day (<c>yyyy-MM-dd</c>) or a UTC-instant hour (<c>yyyy-MM-ddTHH:00</c> local).</summary>
public sealed record AdminProviderBucketDto(string Key, DateTime Start, DateTime End, bool Future);

/// <summary>
/// One metric over the buckets. <paramref name="Unit"/>: count | credits | providerCredits | usd | vnd | percent | ms | minutes.
/// A null value is "not tracked in that bucket" (before tracking began, a source that failed, still to come).
/// </summary>
public sealed record AdminProviderMetricSeriesDto(
    string Key,
    string Unit,
    IReadOnlyList<decimal?> Values,
    string? Note);

/// <summary>A metric over the whole period (latency percentiles are over every call in it, not an average of buckets).</summary>
public sealed record AdminProviderTotalDto(string Key, string Unit, decimal? Value, string? Note);

/// <summary>GET /api/v1/admin/providers/{key}/series.</summary>
public sealed record AdminProviderSeriesDto(
    string Provider,
    AdminInsightRange Range,
    string Granularity,
    string TimeZone,
    IReadOnlyList<AdminProviderBucketDto> Buckets,
    IReadOnlyList<AdminProviderMetricSeriesDto> Metrics,
    IReadOnlyList<AdminProviderTotalDto> Totals);

/// <summary>One slice. <paramref name="Share"/> is its percent of <see cref="AdminProviderBreakdownDto.Total"/>.</summary>
public sealed record AdminProviderBreakdownItemDto(
    string Key,
    string Label,
    decimal Value,
    decimal? Share,
    decimal? CostUsd,
    decimal? CostVnd,
    long? Calls,
    long? Failures);

/// <summary>GET /api/v1/admin/providers/{key}/breakdown. <paramref name="Available"/> false: this split does not exist for the provider, and <paramref name="Note"/> says why.</summary>
public sealed record AdminProviderBreakdownDto(
    string Provider,
    string By,
    AdminInsightRange Range,
    string Unit,
    bool Available,
    decimal Total,
    IReadOnlyList<AdminProviderBreakdownItemDto> Items,
    string? Note);

public sealed record AdminProviderIncidentDto(
    string Name,
    string Impact,
    string Status,
    DateTime StartedAt,
    DateTime? ResolvedAt,
    string? Url);

/// <summary>
/// One local day of the uptime row. <paramref name="Status"/>: operational | degraded | partial_outage |
/// major_outage | no_data. <paramref name="Tracked"/>: our calls were being recorded that day.
/// </summary>
public sealed record AdminProviderUptimeDayDto(
    string Date,
    string Status,
    bool Tracked,
    long Calls,
    long Failures,
    decimal? SuccessRate,
    int? P95Ms,
    IReadOnlyDictionary<string, long> FailuresByClass,
    IReadOnlyList<AdminProviderIncidentDto> Incidents);

/// <summary>GET /api/v1/admin/providers/{key}/uptime.</summary>
public sealed record AdminProviderUptimeDto(
    string Provider,
    string TimeZone,
    IReadOnlyList<AdminProviderUptimeDayDto> Days,
    decimal? UptimePercent,
    string Basis,
    DateTime? TrackedSince,
    AdminProviderStatusPageDto? StatusPage,
    IReadOnlyList<AdminProviderIncidentDto> RecentIncidents,
    string? Note);

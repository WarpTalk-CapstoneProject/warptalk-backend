using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// The admin Providers page (2026-09-25): every external provider WarpTalk pays for, with its usage,
/// cost, live success rate and a 90-day uptime row. Read-only, system-admin only. Definitions live on
/// <see cref="Services.ProviderMetricsCalculator"/>.
/// </summary>
public interface IAdminProvidersService
{
    Task<Result<AdminProvidersOverviewDto>> GetOverviewAsync(string? timeZoneId, CancellationToken ct = default);

    /// <summary><paramref name="granularity"/>: day (default) | hour. NotFound for an unknown provider.</summary>
    Task<Result<AdminProviderSeriesDto>> GetSeriesAsync(
        string provider, AdminInsightsQuery query, string? granularity, string? metrics, CancellationToken ct = default);

    /// <summary><paramref name="by"/>: workspace | service | model | operation | errorClass.</summary>
    Task<Result<AdminProviderBreakdownDto>> GetBreakdownAsync(
        string provider, AdminInsightsQuery query, string? by, CancellationToken ct = default);

    /// <summary>One status per local day for the last <paramref name="days"/> days (1–90, default 90).</summary>
    Task<Result<AdminProviderUptimeDto>> GetUptimeAsync(
        string provider, int? days, string? timeZoneId, CancellationToken ct = default);
}

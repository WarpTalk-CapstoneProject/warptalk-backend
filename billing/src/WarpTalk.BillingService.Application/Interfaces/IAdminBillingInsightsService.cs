using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>
/// Platform admin Insights, billing half (2026-09-17). Read-only; every figure is defined in
/// <see cref="Services.AdminBillingInsightsCalculator"/> and the repository read models it consumes.
/// </summary>
public interface IAdminBillingInsightsService
{
    /// <summary>Period metrics and series. Validation failures return <see cref="ErrorCodes.ValidationError"/>.</summary>
    Task<Result<AdminBillingInsightsDto>> GetInsightsAsync(AdminInsightsQuery query, CancellationToken ct = default);

    /// <summary>
    /// "Right now" figures with no period. <paramref name="timeZoneId"/> (IANA, default
    /// Asia/Ho_Chi_Minh) decides where today, yesterday and this month begin; an unknown id is a
    /// <see cref="ErrorCodes.ValidationError"/>.
    /// </summary>
    Task<Result<AdminBillingSnapshotDto>> GetSnapshotAsync(string? timeZoneId, CancellationToken ct = default);
}

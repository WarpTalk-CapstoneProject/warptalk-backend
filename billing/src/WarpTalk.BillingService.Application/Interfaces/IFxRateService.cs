using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

/// <summary>The USD→VND rate: Stripe's by default, recorded per UTC day, overridable by an admin.</summary>
public interface IFxRateService
{
    /// <summary>Every recorded day, for converting a period at each day's rate.</summary>
    Task<FxRateTable> GetTableAsync(CancellationToken ct = default);

    Task<AdminFxRateStatusDto> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Records today's Stripe FX quote and recent charge conversions (a long backfill the first time),
    /// writes today's manual row while an override is on, and keeps <c>fx_rate_usd_vnd</c> equal to
    /// today's effective rate. <paramref name="force"/> = false skips the quote when today already has one.
    /// Never throws for a Stripe failure: the result carries the error.
    /// </summary>
    Task<AdminFxRefreshResultDto> RefreshAsync(bool force, CancellationToken ct = default);

    Task<Result<AdminFxRateStatusDto>> SetOverrideAsync(decimal rate, CancellationToken ct = default);

    Task<Result<AdminFxRateStatusDto>> ClearOverrideAsync(CancellationToken ct = default);
}

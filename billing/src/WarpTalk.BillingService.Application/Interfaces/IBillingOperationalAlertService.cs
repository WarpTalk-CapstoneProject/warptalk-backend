using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingOperationalAlertService
{
    Task<Result> AlertSettlementFailedAsync(
        SettleUsageChargeRequest request,
        string? error,
        CancellationToken cancellationToken = default);
}

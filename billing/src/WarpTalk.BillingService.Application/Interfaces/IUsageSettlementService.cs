using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageSettlementService
{
    Task<Result<SettleUsageChargeResult>> SettleUsageChargeAsync(
        SettleUsageChargeRequest request,
        CancellationToken cancellationToken = default);
}

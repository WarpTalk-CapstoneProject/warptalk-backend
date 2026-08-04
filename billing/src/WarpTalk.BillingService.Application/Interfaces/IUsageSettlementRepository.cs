using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageSettlementRepository
{
    Task<SettleUsageChargeResult?> ExecuteSettlementAsync(
        SettleUsageChargeRequest request,
        CancellationToken cancellationToken = default);
}

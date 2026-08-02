using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingPolicyService
{
    Task<BillingPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default);
    Task<Result<BillingPolicyDto>> UpdatePolicyAsync(UpdateBillingPolicyRequest request, CancellationToken cancellationToken = default);
}


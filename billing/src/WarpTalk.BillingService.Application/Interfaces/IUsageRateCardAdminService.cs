using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageRateCardAdminService
{
    Task<Result<IReadOnlyList<UsageRateCardDto>>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default);
    Task<Result<UsageRateCardDto>> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default);
    Task<Result<PricingConfigDto>> GetPricingConfigAsync(CancellationToken cancellationToken = default);
    Task<Result<PricingConfigDto>> UpdatePricingConfigAsync(UpdatePricingConfigRequest request, CancellationToken cancellationToken = default);
}

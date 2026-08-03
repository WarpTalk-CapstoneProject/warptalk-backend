using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageRateCardRepository
{

    Task<IReadOnlyList<UsageRateCardDto>> GetActiveRateCardsAsync(CancellationToken cancellationToken = default);
    Task<bool> RateCardIdentityExistsAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default);
    Task<UsageRateCardDto> UpsertRateCardAsync(UpsertUsageRateCardRequest request, CancellationToken cancellationToken = default);
    Task<decimal> ReadPricingConfigValueAsync(string key, decimal defaultValue, CancellationToken cancellationToken = default);
    Task UpsertPricingConfigValueAsync(string key, decimal value, CancellationToken cancellationToken = default);
    
    // Transaction management for multiple config updates
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
}

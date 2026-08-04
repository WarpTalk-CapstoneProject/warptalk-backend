using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IUsageRateCardResolverService
{
    Task<Result<UsageRateCardDto>> ResolveRateCardAsync(
        string chargeType,
        string unit,
        string currency,
        string? sourceLanguageCode,
        string? targetLanguageCode,
        CancellationToken cancellationToken = default);
}

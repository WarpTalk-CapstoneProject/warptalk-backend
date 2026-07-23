using System.Threading;
using System.Threading.Tasks;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingRateService
{
    Result<ServiceRatesDto> GetServiceRates();
    Task<Result<ServiceRatesDto>> UpdateServiceRatesAsync(UpdateServiceRatesRequest request, CancellationToken cancellationToken = default);
}

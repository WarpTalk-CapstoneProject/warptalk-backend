using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingCycleClosingService
{
    Task<Result<int>> CloseDueCyclesAsync(DateTime now, TimeSpan lookback, CancellationToken cancellationToken = default);
}

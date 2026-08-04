using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Interfaces;

public interface IBillingCycleClosingService
{
    Task<Result<int>> CloseDueCyclesAsync(DateTime now, TimeSpan lookback, CancellationToken cancellationToken = default);
    Task<Result<int>> CloseWorkspaceCycleAsync(Guid workspaceId, DateTime now, CancellationToken cancellationToken = default);
}

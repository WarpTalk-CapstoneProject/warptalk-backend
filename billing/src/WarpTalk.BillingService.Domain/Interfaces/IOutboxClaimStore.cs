using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IOutboxClaimStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        int batchSize,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<int> PurgePublishedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default);
}

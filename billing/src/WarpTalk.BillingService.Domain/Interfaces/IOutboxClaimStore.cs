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

    /// <summary>
    /// WT-263: stamps a claimed row as delivered. Separate from <see cref="ClaimAsync"/> so the
    /// window between claiming and publishing is at-least-once rather than at-most-once — a crash
    /// after publishing but before stamping redelivers the event, which consumers absorb because the
    /// payload is a full snapshot, not a delta.
    /// </summary>
    Task MarkPublishedAsync(Guid id, DateTime publishedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a claimed row after a failed publish and records why, so the next sweep retries it.
    /// The row keeps its incremented attempt_count from <see cref="ClaimAsync"/>.
    /// </summary>
    Task ReleaseFailedAsync(Guid id, string error, CancellationToken cancellationToken = default);
}

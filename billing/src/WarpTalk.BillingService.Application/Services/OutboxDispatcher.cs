using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public sealed class OutboxDispatcher(
    IUnitOfWork unitOfWork,
    IOutboxEventPublisher publisher,
    TimeProvider? timeProvider = null,
    IOutboxClaimStore? claimStore = null)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IOutboxClaimStore? _claimStore = claimStore;

    public Task<int> PurgePublishedBeforeAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default) =>
        _claimStore?.PurgePublishedBeforeAsync(cutoffUtc, cancellationToken)
        ?? Task.FromResult(0);

    public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            return 0;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var messages = _claimStore is not null
            ? await _claimStore.ClaimAsync(batchSize, now, cancellationToken)
            : await unitOfWork.OutboxMessages.GetPagedAsync(
                message => message.PublishedAt == null
                    && message.DeadLetteredAt == null
                    && message.AvailableAt <= now
                    && (message.LockedAt == null || message.LockedAt < now - LockTimeout),
                0,
                batchSize,
                query => query.OrderBy(message => message.CreatedAt),
                cancellationToken);

        var publishedCount = 0;
        foreach (var message in messages)
        {
            message.LockedAt = now;
            if (_claimStore is null)
                message.AttemptCount++;

            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                message.PublishedAt = now;
                message.LastError = null;
                publishedCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.LastError = exception.Message;
                if (message.AttemptCount >= 10)
                    message.DeadLetteredAt = now;
                else
                    message.AvailableAt = now + RetryDelay(message.AttemptCount);
            }
            finally
            {
                message.LockedAt = null;
                unitOfWork.OutboxMessages.Update(message);
            }
        }

        if (messages.Count > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return publishedCount;
    }

    private static TimeSpan RetryDelay(int attemptCount)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Max(0, attemptCount - 1)) * 5);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}

using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Services;

public sealed class OutboxReplayService(
    IUnitOfWork unitOfWork,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<bool> ReplayAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var message = await unitOfWork.OutboxMessages.GetByIdAsync(eventId, cancellationToken);
        if (message is null || message.DeadLetteredAt is null)
            return false;

        message.AttemptCount = 0;
        message.AvailableAt = _timeProvider.GetUtcNow().UtcDateTime;
        message.LockedAt = null;
        message.DeadLetteredAt = null;
        message.LastError = null;
        unitOfWork.OutboxMessages.Update(message);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

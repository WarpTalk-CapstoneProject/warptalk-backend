using Microsoft.EntityFrameworkCore;
using WarpTalk.NotificationService.Domain.Entities;
using WarpTalk.NotificationService.Domain.Interfaces;
using WarpTalk.NotificationService.Infrastructure.Persistence;

namespace WarpTalk.NotificationService.Infrastructure.Repositories;

public class NotificationInboxMessageRepository : INotificationInboxMessageRepository
{
    private readonly NotificationDbContext _context;

    public NotificationInboxMessageRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public Task<bool> HasProcessedAsync(Guid eventId, string consumer, CancellationToken ct = default) =>
        _context.Set<NotificationInboxMessage>()
            .AnyAsync(receipt => receipt.EventId == eventId && receipt.Consumer == consumer, ct);

    public async Task AddAsync(NotificationInboxMessage receipt, CancellationToken ct = default) =>
        await _context.Set<NotificationInboxMessage>().AddAsync(receipt, ct);
}

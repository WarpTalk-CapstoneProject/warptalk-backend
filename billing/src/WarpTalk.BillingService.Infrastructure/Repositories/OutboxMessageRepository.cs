using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class OutboxMessageRepository : GenericRepository<OutboxMessage>, IOutboxMessageRepository
{
    public OutboxMessageRepository(BillingDbContext context) : base(context)
    {
    }
}

using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class InboxMessageRepository : GenericRepository<InboxMessage>, IInboxMessageRepository
{
    public InboxMessageRepository(BillingDbContext context) : base(context)
    {
    }
}

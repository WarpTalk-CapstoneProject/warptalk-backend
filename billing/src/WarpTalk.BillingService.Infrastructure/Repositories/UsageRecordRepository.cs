using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class UsageRecordRepository : GenericRepository<UsageRecord>, IUsageRecordRepository
{
    public UsageRecordRepository(BillingDbContext context) : base(context)
    {
    }
}

using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class RefundRepository : GenericRepository<Refund>, IRefundRepository
{
    public RefundRepository(BillingDbContext context) : base(context)
    {
    }
}

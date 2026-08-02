using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class PlanRepository : GenericRepository<Plan>, IPlanRepository
{
    public PlanRepository(BillingDbContext context) : base(context)
    {
    }
}

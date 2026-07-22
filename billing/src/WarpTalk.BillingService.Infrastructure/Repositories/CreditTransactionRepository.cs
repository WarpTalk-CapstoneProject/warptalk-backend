using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class CreditTransactionRepository : GenericRepository<CreditTransaction>, ICreditTransactionRepository
{
    public CreditTransactionRepository(BillingDbContext context) : base(context)
    {
    }
}

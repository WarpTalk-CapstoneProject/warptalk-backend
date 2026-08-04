using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class SalesInquiryRepository : GenericRepository<SalesInquiry>, ISalesInquiryRepository
{
    public SalesInquiryRepository(BillingDbContext context) : base(context)
    {
    }
}

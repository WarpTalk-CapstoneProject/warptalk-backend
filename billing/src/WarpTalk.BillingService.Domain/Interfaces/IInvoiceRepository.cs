using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IInvoiceRepository : IGenericRepository<Invoice>
{
    Task<PagedResult<Invoice>> GetPageAsync(PageRequest page, Guid? workspaceId, CancellationToken cancellationToken = default);
}

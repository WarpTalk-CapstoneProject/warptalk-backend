using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IInvoiceRepository : IGenericRepository<Invoice>
{
    Task<PagedResult<Invoice>> GetPageAsync(PageRequest page, Guid? workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Invoice>> GetOverdueOpenInvoicesAsync(DateTime now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Invoice>> GetOpenInvoicesDueBeforeAsync(DateTime threshold, CancellationToken cancellationToken = default);
}

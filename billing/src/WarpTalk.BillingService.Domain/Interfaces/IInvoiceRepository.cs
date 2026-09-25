using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Domain.Interfaces;

public interface IInvoiceRepository : IGenericRepository<Invoice>
{
    Task<PagedResult<Invoice>> GetPageAsync(PageRequest page, Guid? workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the platform-wide invoice list, filtered and ordered in SQL, with Payment and
    /// Payment.Subscription loaded (the workspace id comes from there).
    /// </summary>
    Task<PagedResult<Invoice>> GetGlobalPageAsync(GlobalInvoiceFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Invoice>> GetOverdueOpenInvoicesAsync(DateTime now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Invoice>> GetOpenInvoicesDueBeforeAsync(DateTime threshold, CancellationToken cancellationToken = default);

    /// <summary>Admin Insights: every issued invoice (open or issued) whose payment is not paid.</summary>
    Task<IReadOnlyList<OutstandingInvoiceRow>> GetOutstandingAsync(CancellationToken cancellationToken = default);

    /// <summary>G12 inbox: issued, unpaid invoices (same rule as <see cref="GetOutstandingAsync"/>), oldest due first.</summary>
    Task<IReadOnlyList<InboxInvoiceRow>> GetOutstandingForInboxAsync(int take, CancellationToken cancellationToken = default);

    /// <summary><see cref="GetOutstandingAsync"/> for one workspace.</summary>
    Task<IReadOnlyList<OutstandingInvoiceRow>> GetOutstandingForWorkspaceAsync(
        Guid workspaceId, CancellationToken cancellationToken = default);
}

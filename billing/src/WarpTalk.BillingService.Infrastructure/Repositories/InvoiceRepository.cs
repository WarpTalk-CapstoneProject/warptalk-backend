using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

public class InvoiceRepository : GenericRepository<Invoice>, IInvoiceRepository
{
    public InvoiceRepository(BillingDbContext context) : base(context)
    {
    }

    public async Task<PagedResult<Invoice>> GetPageAsync(PageRequest page, Guid? workspaceId, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(page);
        var filtered = workspaceId.HasValue
            ? _dbSet.Where(i => i.Payment.Subscription.WorkspaceId == workspaceId.Value)
            : _dbSet.AsQueryable();

        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .Include(i => i.Payment)
            .ThenInclude(p => p.Subscription)
            .OrderByDescending(i => i.CreatedAt)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Invoice>(items, total, normalized.PageNumber, normalized.PageSize);
    }

    public async Task<IReadOnlyList<Invoice>> GetOverdueOpenInvoicesAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(i => i.Payment)
            .ThenInclude(p => p.Subscription)
            .ThenInclude(s => s.Plan)
            .Where(i =>
                i.DueAt != null &&
                i.DueAt < now &&
                i.Status != InvoiceConstants.InvoiceStatuses.Paid &&
                i.Status != InvoiceConstants.InvoiceStatuses.Void)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Invoice>> GetOpenInvoicesDueBeforeAsync(DateTime threshold, CancellationToken cancellationToken = default)
    {
        return await _dbSet
            .Include(i => i.Payment)
            .ThenInclude(p => p.Subscription)
            .ThenInclude(s => s.Plan)
            .Where(i =>
                i.DueAt != null &&
                i.DueAt <= threshold &&
                i.Status != InvoiceConstants.InvoiceStatuses.Paid &&
                i.Status != InvoiceConstants.InvoiceStatuses.Void)
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<OutstandingInvoiceRow>> GetOutstandingAsync(CancellationToken cancellationToken = default)
        => ReadOutstandingAsync(_dbSet.AsNoTracking(), cancellationToken);

    public Task<IReadOnlyList<OutstandingInvoiceRow>> GetOutstandingForWorkspaceAsync(
        Guid workspaceId, CancellationToken cancellationToken = default)
        => ReadOutstandingAsync(
            _dbSet.AsNoTracking().Where(i => i.Payment.Subscription.WorkspaceId == workspaceId),
            cancellationToken);

    private static async Task<IReadOnlyList<OutstandingInvoiceRow>> ReadOutstandingAsync(
        IQueryable<Invoice> source, CancellationToken cancellationToken)
    {
        var rows = await source
            .Where(i =>
                i.Status != InvoiceConstants.InvoiceStatuses.Paid
                && i.Status != InvoiceConstants.InvoiceStatuses.Void
                && i.Status != InvoiceConstants.InvoiceStatuses.Uncollectible
                && i.Status != InvoiceConstants.InvoiceStatuses.Draft
                && i.Payment.Status != PaymentConstants.PaymentStatuses.Paid)
            .Select(i => new { i.Id, i.Payment.Subscription.WorkspaceId, i.Total, i.Currency, i.DueAt })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new OutstandingInvoiceRow(r.Id, r.WorkspaceId, r.Total, r.Currency, r.DueAt)).ToList();
    }
}

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

    public async Task<PagedResult<Invoice>> GetGlobalPageAsync(GlobalInvoiceFilter filter, CancellationToken cancellationToken = default)
    {
        var normalized = RepositoryPaging.Normalize(filter.Page);
        var filtered = ApplyGlobalFilters(_dbSet, filter);

        var total = await filtered.CountAsync(cancellationToken);
        var items = await ApplyGlobalSort(filtered, filter.Sort)
            .Include(i => i.Payment)
            .ThenInclude(p => p.Subscription)
            .Skip(normalized.Skip)
            .Take(normalized.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Invoice>(items, total, normalized.PageNumber, normalized.PageSize);
    }

    /// <summary>
    /// The global list's WHERE clause. Public so tests can run it over plain rows and ask Npgsql
    /// to translate it (ToQueryString) without a database; the invoice-number search uses ILike,
    /// which only the latter can evaluate.
    /// </summary>
    public static IQueryable<Invoice> ApplyGlobalFilters(IQueryable<Invoice> query, GlobalInvoiceFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            if (Guid.TryParse(term, out var invoiceId))
            {
                query = query.Where(i => i.Id == invoiceId);
            }
            else
            {
                var pattern = $"%{term}%";
                query = query.Where(i => EF.Functions.ILike(i.InvoiceNumber, pattern));
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status;
            query = query.Where(i => i.Status == status);
        }

        if (filter.WorkspaceId is { } workspaceId)
            query = query.Where(i => i.Payment.Subscription.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(filter.Currency))
        {
            // Upper-cased on both sides: the column is CHAR(3) and older rows were written with
            // Stripe's lowercase codes.
            var currency = filter.Currency;
            query = query.Where(i => i.Currency.ToUpper() == currency);
        }

        // Half-open on issued_at: inclusive from, exclusive to.
        if (filter.FromDate is { } from)
            query = query.Where(i => i.IssuedAt >= from);

        if (filter.ToDate is { } to)
            query = query.Where(i => i.IssuedAt < to);

        if (filter.MinTotal is { } minTotal)
            query = query.Where(i => i.Total >= minTotal);

        if (filter.MaxTotal is { } maxTotal)
            query = query.Where(i => i.Total <= maxTotal);

        return query;
    }

    /// <summary>The global list's ORDER BY; every key ends on Id so pages are stable.</summary>
    public static IOrderedQueryable<Invoice> ApplyGlobalSort(IQueryable<Invoice> query, string sort) => sort switch
    {
        GlobalInvoiceSorts.IssuedAsc => query.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id),
        GlobalInvoiceSorts.TotalDesc => query.OrderByDescending(i => i.Total).ThenByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id),
        GlobalInvoiceSorts.TotalAsc => query.OrderBy(i => i.Total).ThenByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id),
        // Soonest due first; an invoice with no due date is not the most urgent one, and
        // PostgreSQL would otherwise sort NULL first on nothing but its own default.
        GlobalInvoiceSorts.DueAsc => query.OrderBy(i => i.DueAt == null).ThenBy(i => i.DueAt).ThenByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id),
        // issued_desc — the list's historical order (created_at DESC).
        _ => query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id),
    };

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

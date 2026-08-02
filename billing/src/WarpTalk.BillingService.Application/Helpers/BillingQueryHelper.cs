using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Application.Helpers;

public static class BillingQueryHelper
{
    public static PageRequest ToPageRequest(PaginationQuery query)
        => new(query.PageNumber, query.PageSize);

    public static CreditTransactionHistoryFilter ToCreditTransactionHistoryFilter(
        CreditHistoryQuery query,
        IReadOnlyCollection<Guid>? subscriptionIds)
    {
        return new CreditTransactionHistoryFilter(
            ToPageRequest(query),
            subscriptionIds,
            query.WorkspaceId,
            query.Type,
            query.FromDate,
            query.ToDate,
            query.MinAmount,
            query.MaxAmount);
    }

    public static Guid[] GetWorkspaceIds(IEnumerable<Invoice> invoices)
    {
        return invoices
            .Select(i => i.Payment.Subscription.WorkspaceId)
            .Distinct()
            .ToArray();
    }

    public static Guid[] GetWorkspaceIds(IEnumerable<CreditTransactionDto> transactions)
    {
        return transactions
            .Where(d => d.WorkspaceId.HasValue && d.WorkspaceId != Guid.Empty)
            .Select(d => d.WorkspaceId!.Value)
            .Distinct()
            .ToArray();
    }

    public static Guid[] GetWorkspaceIds(IEnumerable<SubscriptionDto> subscriptions)
    {
        return subscriptions
            .Where(i => i.WorkspaceId.HasValue && i.WorkspaceId != Guid.Empty)
            .Select(i => i.WorkspaceId!.Value)
            .Distinct()
            .ToArray();
    }

    public static List<InvoiceDto> ApplyWorkspaceNames(
        IReadOnlyList<InvoiceDto> invoices,
        IReadOnlyDictionary<Guid, string> workspaceNames)
    {
        return invoices
            .Select(i => Guid.TryParse(i.WorkspaceId, out var workspaceId) && workspaceNames.TryGetValue(workspaceId, out var workspaceName)
                ? i with { WorkspaceName = workspaceName }
                : i)
            .ToList();
    }

    public static List<CreditTransactionDto> ApplyWorkspaceNames(
        IReadOnlyList<CreditTransactionDto> transactions,
        IReadOnlyDictionary<Guid, string> workspaceNames)
    {
        return transactions
            .Select(d => d.WorkspaceId.HasValue && workspaceNames.TryGetValue(d.WorkspaceId.Value, out var workspaceName)
                ? d with { WorkspaceName = workspaceName }
                : d)
            .ToList();
    }

    public static List<SubscriptionDto> ApplyWorkspaceNames(
        IReadOnlyList<SubscriptionDto> subscriptions,
        IReadOnlyDictionary<Guid, string> workspaceNames)
    {
        return subscriptions
            .Select(i => i.WorkspaceId.HasValue && workspaceNames.TryGetValue(i.WorkspaceId.Value, out var workspaceName)
                ? i with { WorkspaceName = workspaceName }
                : i)
            .ToList();
    }

}

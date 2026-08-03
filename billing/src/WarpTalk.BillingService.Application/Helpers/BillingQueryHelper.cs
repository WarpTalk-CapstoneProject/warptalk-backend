using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Models;
using WarpTalk.Shared;

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

    public static Guid[] GetWorkspaceIds<T>(IEnumerable<T> items, Func<T, Guid?> getWorkspaceId)
    {
        return items
            .Select(getWorkspaceId)
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
    }

    public static List<T> ApplyWorkspaceNames<T>(
        IReadOnlyList<T> items,
        IReadOnlyDictionary<Guid, string> workspaceNames,
        Func<T, Guid?> getWorkspaceId,
        Func<T, string, T> withWorkspaceName)
    {
        return items
            .Select(i =>
            {
                var id = getWorkspaceId(i);
                if (id.HasValue && workspaceNames.TryGetValue(id.Value, out var workspaceName))
                {
                    return withWorkspaceName(i, workspaceName);
                }
                return i;
            })
            .ToList();
    }

}

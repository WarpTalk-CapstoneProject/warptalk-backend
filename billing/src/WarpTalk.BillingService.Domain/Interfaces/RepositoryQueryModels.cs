using System;
using System.Collections.Generic;

namespace WarpTalk.BillingService.Domain.Interfaces;

public sealed record PageRequest(int PageNumber, int PageSize);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record CreditTransactionHistoryFilter(
    PageRequest Page,
    IReadOnlyCollection<Guid>? SubscriptionIds = null,
    Guid? WorkspaceId = null,
    string? Type = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int? MinAmount = null,
    int? MaxAmount = null);

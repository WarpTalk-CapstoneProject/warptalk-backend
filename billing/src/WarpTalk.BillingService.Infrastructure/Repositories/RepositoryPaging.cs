using System;
using WarpTalk.BillingService.Domain.Interfaces;

namespace WarpTalk.BillingService.Infrastructure.Repositories;

internal static class RepositoryPaging
{
    public static NormalizedPage Normalize(PageRequest page)
    {
        var pageNumber = Math.Max(1, page.PageNumber);
        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        return new NormalizedPage(pageNumber, pageSize, (pageNumber - 1) * pageSize);
    }
}

internal sealed record NormalizedPage(int PageNumber, int PageSize, int Skip);

using System;

namespace WarpTalk.BillingService.Application.DTOs;

public record PaginationQuery(
    int PageNumber = 1,
    int PageSize = 20
);

public record CreditHistoryQuery(
    Guid? WorkspaceId = null,
    string? Type = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int? MinAmount = null,
    int? MaxAmount = null
) : PaginationQuery;

public record BillingReportQuery(
    int Month,
    int Year
);

public record UsageChartQuery(
    int Year,
    int Days = 30,
    int Limit = 5
);

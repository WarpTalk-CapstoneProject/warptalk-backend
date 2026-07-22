using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarpTalk.BillingService.Application.DTOs;

// ============================================================================
// IDEMPOTENCY
// ============================================================================

public record IdempotencyKey(string Key, string Operation, string RequestHash);

// ============================================================================
// PAGINATION
// ============================================================================

public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages)
{
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;

    public static PaginatedResponse<T> Create(IReadOnlyList<T> items, int totalCount, int pageNumber, int pageSize)
    {
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        return new PaginatedResponse<T>(items, pageNumber, pageSize, totalCount, totalPages);
    }
}

// ============================================================================
// ERROR RESPONSE
// ============================================================================

public record ErrorDetailDto(
    string Code,
    string Message,
    string? Details = null,
    DateTime Timestamp = default)
{
    public ErrorDetailDto(string code, string message, string? details = null)
        : this(code, message, details, DateTime.UtcNow) { }
}

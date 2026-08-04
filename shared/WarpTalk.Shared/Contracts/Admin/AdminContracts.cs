using System;
using System.Collections.Generic;
using System.Linq;

namespace WarpTalk.Shared.Contracts.Admin;

/// <summary>
/// Shared request/response shapes for <c>~/api/v1/admin/*</c> (WT-205), so every admin endpoint
/// pages, filters, sorts, and reports money the same way regardless of which service serves it.
/// </summary>
public static class AdminContractDefaults
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

/// <summary>Paging request. Bind with <c>[FromQuery]</c> and always call <see cref="Normalize"/>.</summary>
public record AdminPageRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = AdminContractDefaults.DefaultPageSize;

    /// <summary>
    /// Clamps rather than rejecting: an out-of-range page size is a client bug, not a reason to
    /// fail the request, but an unbounded one is a way to make an aggregation query expensive.
    /// </summary>
    public (int Page, int PageSize) Normalize(
        int defaultPageSize = AdminContractDefaults.DefaultPageSize,
        int maxPageSize = AdminContractDefaults.MaxPageSize)
    {
        var page = Page <= 0 ? 1 : Page;
        var pageSize = PageSize <= 0 ? defaultPageSize : Math.Min(PageSize, maxPageSize);
        return (page, pageSize);
    }
}

/// <summary>Envelope for every paginated admin response.</summary>
public record AdminPagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>
/// An explicit, inclusive-start/exclusive-end reporting window. Admin read APIs must not infer
/// a range — an unstated window silently changes what a number means.
/// </summary>
public record AdminDateRange
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    /// <summary>
    /// Normalizes to UTC and validates ordering plus a maximum span. Returns false with a
    /// caller-facing message rather than throwing.
    /// </summary>
    public bool TryNormalize(int maxSpanDays, out DateTime from, out DateTime to, out string? error)
    {
        var utcTo = (To ?? DateTime.UtcNow).ToUniversalTime();
        var utcFrom = (From ?? utcTo.AddDays(-30)).ToUniversalTime();

        if (utcFrom >= utcTo)
        {
            from = default;
            to = default;
            error = "'from' must be earlier than 'to'.";
            return false;
        }

        if ((utcTo - utcFrom).TotalDays > maxSpanDays)
        {
            from = default;
            to = default;
            error = $"Date range must not exceed {maxSpanDays} days.";
            return false;
        }

        from = utcFrom;
        to = utcTo;
        error = null;
        return true;
    }
}

/// <summary>
/// A monetary amount that always states its currency. A bare decimal on an admin dashboard is
/// ambiguous the moment a second currency exists.
/// </summary>
public readonly record struct AdminMoney(decimal Amount, string Currency)
{
    public const string DefaultCurrency = "USD";

    /// <summary>
    /// Rounds half away from zero to the given scale — deterministic and independent of the
    /// banker's rounding .NET would otherwise apply, so two services never disagree by a cent.
    /// </summary>
    public static AdminMoney Of(decimal amount, string currency = DefaultCurrency, int scale = 2) =>
        new(Math.Round(amount, scale, MidpointRounding.AwayFromZero), currency);
}

/// <summary>Credits are whole units; this exists so responses never ship a bare number.</summary>
public readonly record struct AdminCredits(long Amount)
{
    public static AdminCredits Of(long amount) => new(amount);
}

/// <summary>Resolution of a client-supplied sort key against the keys an endpoint actually supports.</summary>
public static class AdminSort
{
    /// <summary>
    /// Returns false for an unknown key instead of silently falling back — a caller who asked to
    /// sort by something the endpoint does not support should learn that, not receive a page
    /// ordered by something else.
    /// </summary>
    public static bool TryResolve(
        string? requested,
        IEnumerable<string> allowed,
        string fallback,
        out string sort)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            sort = fallback;
            return true;
        }

        var normalized = requested.Trim().ToLowerInvariant();
        if (allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            sort = normalized;
            return true;
        }

        sort = fallback;
        return false;
    }
}

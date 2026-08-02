using System.Linq.Expressions;
using System.Text.Json;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Application.Helpers;

public static class SalesInquiryHelper
{
    public static Result ValidateCreate(CreateSalesInquiryRequest request)
    {
        if (!request.Consent)
            return Result.Failure(SalesInquiryConstants.Errors.ConsentRequired, ErrorCodes.ValidationError);

        if (request.FeatureInterests is null || request.FeatureInterests.Count == 0)
            return Result.Failure(SalesInquiryConstants.Errors.FeatureInterestRequired, ErrorCodes.ValidationError);

        if (request.TargetLanguages is null || request.TargetLanguages.Count == 0)
            return Result.Failure(SalesInquiryConstants.Errors.TargetLanguageRequired, ErrorCodes.ValidationError);

        if (string.IsNullOrWhiteSpace(request.FirstName) ||
            string.IsNullOrWhiteSpace(request.LastName) ||
            string.IsNullOrWhiteSpace(request.WorkEmail) ||
            string.IsNullOrWhiteSpace(request.Company) ||
            string.IsNullOrWhiteSpace(request.RequestType) ||
            string.IsNullOrWhiteSpace(request.CurrentMonthlyMeetingVolume))
        {
            return Result.Failure(SalesInquiryConstants.Errors.RequiredFieldsMissing, ErrorCodes.ValidationError);
        }

        var requestedMonthlyCredits = TryReadLong(request.PricingEstimate, "requestedMonthlyCredits");
        if (requestedMonthlyCredits is { } credits &&
            (credits < 1 || credits > SalesInquiryConstants.Defaults.MaxRequestedMonthlyCredits))
        {
            return Result.Failure(SalesInquiryConstants.Errors.RequestedMonthlyCreditsInvalid, ErrorCodes.ValidationError);
        }

        var requestedWorkspaceMembers = TryReadLong(request.PricingEstimate, "requestedWorkspaceMembers");
        if (requestedWorkspaceMembers is { } members &&
            (members < 1 || members > SalesInquiryConstants.Defaults.MaxRequestedWorkspaceMembers))
        {
            return Result.Failure(SalesInquiryConstants.Errors.RequestedWorkspaceMembersInvalid, ErrorCodes.ValidationError);
        }

        return Result.Success();
    }

    public static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        var normalized = status.Trim().ToLowerInvariant();
        return SalesInquiryConstants.Statuses.All.Contains(normalized) ? normalized : null;
    }

    public static void ApplyStatus(SalesInquiry inquiry, string status, DateTime? timestamp = null)
    {
        var now = timestamp ?? DateTime.UtcNow;
        inquiry.Status = status;
        inquiry.UpdatedAt = now;

        if (status == SalesInquiryConstants.Statuses.Converted)
        {
            inquiry.ConvertedAt ??= now;
            inquiry.ClosedAt = null;
            return;
        }

        if (status == SalesInquiryConstants.Statuses.Closed)
            inquiry.ClosedAt ??= now;
    }

    public static (int Page, int PageSize, int Skip) NormalizePagination(SalesInquiryQuery query)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, SalesInquiryConstants.Defaults.MaxPageSize);
        return (page, pageSize, (page - 1) * pageSize);
    }

    public static Expression<Func<SalesInquiry, bool>> BuildQueryPredicate(SalesInquiryQuery query, string? normalizedStatus)
    {
        var search = query.Search?.Trim().ToLowerInvariant();
        var workspaceId = query.WorkspaceId;

        return inquiry =>
            (normalizedStatus == null || inquiry.Status == normalizedStatus) &&
            (workspaceId == null || inquiry.WorkspaceId == workspaceId) &&
            (search == null ||
             inquiry.WorkEmail.ToLower().Contains(search) ||
             inquiry.Company.ToLower().Contains(search) ||
             inquiry.FirstName.ToLower().Contains(search) ||
             inquiry.LastName.ToLower().Contains(search));
    }

    private static long? TryReadLong(object? source, string propertyName)
    {
        if (source is null)
            return null;

        if (source is JsonElement element)
            return TryReadLong(element, propertyName);

        if (source is IDictionary<string, object?> dictionary &&
            dictionary.TryGetValue(propertyName, out var value))
        {
            return TryReadLongValue(value);
        }

        try
        {
            return TryReadLong(JsonSerializer.SerializeToElement(source), propertyName);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return TryReadLongValue(property);
    }

    private static long? TryReadLongValue(object? value)
    {
        return value switch
        {
            null => null,
            long longValue => longValue,
            int intValue => intValue,
            decimal decimalValue when decimalValue == decimal.Truncate(decimalValue) => (long)decimalValue,
            double doubleValue when doubleValue % 1 == 0 => (long)doubleValue,
            string stringValue when long.TryParse(stringValue, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } text when long.TryParse(text.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}

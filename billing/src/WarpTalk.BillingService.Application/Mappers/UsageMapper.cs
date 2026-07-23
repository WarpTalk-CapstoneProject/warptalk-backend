using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class UsageMapper
{
    public static CreditTransaction ToCreditTransaction(this RecordUsageRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = request.UserId,
        Amount = -request.CreditsConsumed,
        Type = BillingConstants.TransactionTypes.Consume,
        Description = $"AI Usage: {request.UsageType} by User {request.UserId}",
        ReferenceType = BillingConstants.ReferenceTypes.UsageRecord,
        ReferenceId = request.TranslationRoomId,
        BalanceAfter = sub.CreditsRemaining,
        CreatedAt = DateTime.UtcNow
    };

    public static UsageRecord ToUsageRecord(this RecordUsageRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = request.UserId,
        WorkspaceId = request.HostWorkspaceId,
        TranslationRoomId = request.TranslationRoomId,
        SegmentId = request.SegmentId,
        UsageType = request.UsageType,
        Unit = request.Unit,
        Quantity = request.Quantity,
        CreditsConsumed = request.CreditsConsumed,
        DurationSeconds = request.DurationSeconds,
        Details = request.Details,
        RecordedAt = DateTime.UtcNow
    };

    public static UsageRecord ToUsageRecord(this ConsumeCreditsRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = sub.UserId,
        WorkspaceId = sub.WorkspaceId,
        TranslationRoomId = request.ReferenceId,
        UsageType = Helpers.CreditRatesHelper.GetUsageType(request.ReferenceType),
        Unit = "request",
        Quantity = 1,
        CreditsConsumed = request.Amount,
        RecordedAt = DateTime.UtcNow
    };
}

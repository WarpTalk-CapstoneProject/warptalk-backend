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
        Type = "consume",
        Description = $"AI Usage: {request.UsageType} by User {request.UserId}",
        ReferenceType = "usage_record",
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
}

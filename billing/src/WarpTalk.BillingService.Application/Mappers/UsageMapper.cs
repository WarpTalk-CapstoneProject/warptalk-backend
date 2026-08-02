using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Application.Interfaces;

namespace WarpTalk.BillingService.Application.Mappers;

public static class UsageMapper
{
    public static CreditTransaction ToCreditTransaction(this RecordUsageRequest request, Subscription sub) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = sub.Id,
        UserId = request.UserId,
        Amount = -request.CreditsConsumed,
        Type = TransactionConstants.TransactionTypes.Consume,
        Description = string.Format(BillingMessageConstants.UsageMessages.AiUsageTemplate, request.UsageType, request.UserId),
        ReferenceType = TransactionConstants.ReferenceTypes.UsageRecord,
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
        Unit = UsageConstants.UsageUnits.Request,
        Quantity = 1,
        CreditsConsumed = request.Amount,
        RecordedAt = DateTime.UtcNow
    };

    public static TempUsageLogDto CreateTempUsageLogDto(CreateTempUsageLogRequest request) => new()
    {
        SubscriptionId = request.SubscriptionId,
        UserId = request.UserId,
        WorkspaceId = request.WorkspaceId,
        UsageType = request.UsageType,
        ChargeType = request.ChargeType,
        ReferenceId = request.ReferenceId,
        ReferenceType = request.ReferenceType,
        Quantity = (double)request.Quantity,
        Unit = request.Unit,
        CreditsConsumed = request.CreditsConsumed,
        IdempotencyKey = request.IdempotencyKey,
        Details = request.Details,
        TranslationRoomId = request.TranslationRoomId,
        TranscriptSegmentId = request.TranscriptSegmentId,
        PricingRateCardId = request.PricingRateCardId,
        UnitPriceSnapshot = request.UnitPriceSnapshot,
        Provider = request.Provider,
        Model = request.Model,
        CreatedAt = DateTime.UtcNow
    };

    public static UsageRecord CreateAggregatedUsageRecord(CreateAggregatedUsageRecordRequest request) => new()
    {
        Id = Guid.NewGuid(),
        SubscriptionId = request.SubscriptionId,
        UserId = null, // Aggregated records do not belong to a specific user
        WorkspaceId = request.WorkspaceId,
        UsageType = request.UsageType,
        Quantity = request.Quantity,
        Unit = request.Unit,
        CreditsConsumed = request.CreditsConsumed,
        Details = request.Details,
        RecordedAt = DateTime.UtcNow
    };
}

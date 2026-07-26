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

    public static RecordUsageRequest ToRecordUsageRequest(this ChargeVoiceCloneRequest request)
    {
        int credits = request.IsAdvanced ? UsageConstants.VoiceCloneCosts.AdvancedProfile : UsageConstants.VoiceCloneCosts.StandardProfile;
        return new RecordUsageRequest(
            HostWorkspaceId: request.HostWorkspaceId,
            UserId: request.UserId,
            UsageType: UsageConstants.UsageTypes.VoiceCloning,
            Unit: UsageConstants.UsageUnits.Profile,
            Quantity: 1,
            CreditsConsumed: credits,
            DurationSeconds: null,
            Details: request.IsAdvanced ? UsageConstants.UsageDetails.AdvancedVoiceClone : UsageConstants.UsageDetails.StandardVoiceClone
        );
    }

    /// <summary>
    /// Converts ChargeAiAssistantRequest to RecordUsageRequest using HARD-CODED fallback rates.
    /// Prefer the overload that accepts explicit rates (from IBillingRateService) to support Admin-configurable pricing.
    /// </summary>
    public static RecordUsageRequest ToRecordUsageRequest(this ChargeAiAssistantRequest request)
        => request.ToRecordUsageRequest(inputRatePer1KTokens: 0.5, outputRatePer1KTokens: 2.0);

    /// <summary>
    /// Converts ChargeAiAssistantRequest to RecordUsageRequest using Admin-configurable rates.
    /// Formula: credits = ceil((inputTokens / 1000 * rateInput) + (outputTokens / 1000 * rateOutput))
    /// </summary>
    public static RecordUsageRequest ToRecordUsageRequest(
        this ChargeAiAssistantRequest request,
        double inputRatePer1KTokens,
        double outputRatePer1KTokens)
    {
        int credits = (int)Math.Max(1, Math.Ceiling(
            (request.InputTokens / 1000.0) * inputRatePer1KTokens +
            (request.OutputTokens / 1000.0) * outputRatePer1KTokens
        ));
        return new RecordUsageRequest(
            HostWorkspaceId: request.HostWorkspaceId,
            UserId: request.UserId,
            UsageType: UsageConstants.UsageTypes.AiAssistant,
            Unit: UsageConstants.UsageUnits.Token,
            Quantity: request.InputTokens + request.OutputTokens,
            CreditsConsumed: credits,
            DurationSeconds: null,
            Details: string.Format(BillingMessageConstants.UsageMessages.AiAssistantDetailsTemplate, request.FeatureName, request.ProviderModel ?? "unknown", request.InputTokens, inputRatePer1KTokens, request.OutputTokens, outputRatePer1KTokens)
        );
    }

    public static RecordUsageRequest ToRecordUsageRequest(this ChargeDocumentTranslationRequest request)
    {
        int credits = (int)Math.Max(1, Math.Ceiling((request.CharacterCount / 1000.0) * 1.0));
        return new RecordUsageRequest(
            HostWorkspaceId: request.HostWorkspaceId,
            UserId: request.UserId,
            UsageType: UsageConstants.UsageTypes.DocumentTranslation,
            Unit: UsageConstants.UsageUnits.Character,
            Quantity: request.CharacterCount,
            CreditsConsumed: credits,
            DurationSeconds: null,
            Details: string.Format(BillingMessageConstants.UsageMessages.DocumentTranslationTemplate, request.TargetLanguage)
        );
    }

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

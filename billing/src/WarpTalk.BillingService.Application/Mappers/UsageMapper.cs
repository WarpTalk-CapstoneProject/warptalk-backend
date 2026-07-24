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
        Type = TransactionConstants.TransactionTypes.Consume,
        Description = $"AI Usage: {request.UsageType} by User {request.UserId}",
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

    public static RecordUsageRequest ToRecordUsageRequest(this ChargeAiAssistantRequest request)
    {
        int credits = (int)Math.Max(1, Math.Ceiling((request.InputTokens / 1000.0) * 0.5 + (request.OutputTokens / 1000.0) * 2.0));
        return new RecordUsageRequest(
            HostWorkspaceId: request.HostWorkspaceId,
            UserId: request.UserId,
            UsageType: UsageConstants.UsageTypes.AiAssistant,
            Unit: UsageConstants.UsageUnits.Token,
            Quantity: request.InputTokens + request.OutputTokens,
            CreditsConsumed: credits,
            DurationSeconds: null,
            Details: $"AI Assistant: {request.FeatureName} (In: {request.InputTokens}, Out: {request.OutputTokens})"
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
            Details: $"Document Translation to {request.TargetLanguage}"
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
        Unit = "request",
        Quantity = 1,
        CreditsConsumed = request.Amount,
        RecordedAt = DateTime.UtcNow
    };
}

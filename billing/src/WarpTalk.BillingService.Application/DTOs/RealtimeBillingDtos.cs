using System;
using WarpTalk.BillingService.Domain.Constants;

namespace WarpTalk.BillingService.Application.DTOs;

public record ReserveCreditsRequest(
    Guid HostWorkspaceId,
    string IdempotencyKey,
    int ParticipantCount = 2,
    string MediaStreamType = HelperConstants.CreditRates.MediaStreamTypes.VideoSd
);

public record CreditReservationDto(
    Guid Id,
    Guid SubscriptionId,
    string IdempotencyKey,
    int Amount,
    string Status,
    DateTime ExpiresAt
);

public record ReservationCostRequest(
    double AudioSeconds,
    bool IsVoiceClone,
    double SttRateMin,
    double TranslationRateMin,
    double TtsRateMin,
    double VoiceCloneRateMin
);

public record CreateTempUsageLogRequest(
    Guid SubscriptionId,
    string? UserId,
    Guid WorkspaceId,
    string UsageType,
    string ChargeType,
    Guid? ReferenceId,
    string ReferenceType,
    decimal Quantity,
    string Unit,
    int CreditsConsumed,
    string IdempotencyKey,
    string Details,
    string? TranslationRoomId = null,
    Guid? TranscriptSegmentId = null,
    Guid? PricingRateCardId = null,
    decimal? UnitPriceSnapshot = null,
    string? Provider = null,
    string? Model = null
);

public record CreateAggregatedUsageRecordRequest(
    Guid SubscriptionId,
    Guid WorkspaceId,
    string UsageType,
    decimal Quantity,
    string Unit,
    int CreditsConsumed,
    string Details
);

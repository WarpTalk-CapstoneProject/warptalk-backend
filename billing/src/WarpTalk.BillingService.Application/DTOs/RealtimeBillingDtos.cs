using System;

namespace WarpTalk.BillingService.Application.DTOs;

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

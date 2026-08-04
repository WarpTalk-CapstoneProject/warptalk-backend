namespace WarpTalk.BillingService.Application.DTOs;

public sealed record SettleUsageChargeRequest(
    Guid SubscriptionId,
    Guid? UserId,
    Guid WorkspaceId,
    string UsageType,
    string ChargeType,
    Guid? ReferenceId,
    string ReferenceType,
    Guid? TranslationRoomId,
    Guid? TranscriptSegmentId,
    decimal Quantity,
    string Unit,
    int CreditsConsumed,
    string IdempotencyKey,
    Guid? PricingRateCardId,
    decimal? UnitPriceSnapshot,
    string Currency,
    string? Details);

public sealed record SettleUsageChargeResult(
    bool Applied,
    Guid? TransactionId,
    Guid? UsageRecordId,
    int? BalanceAfter,
    string? ServiceState,
    string? SuspendedReason,
    bool JustEnteredOverage = false);

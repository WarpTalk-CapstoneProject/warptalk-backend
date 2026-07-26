using System.Text.Json;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Mappers;

public static class UsageSettlementMapper
{
    public static SettleUsageChargeRequest ToSettlementRequest(this RecordUsageRequest request, Subscription subscription)
        => new(
            SubscriptionId: subscription.Id,
            UserId: request.UserId,
            WorkspaceId: request.HostWorkspaceId,
            UsageType: request.UsageType,
            ChargeType: request.UsageType,
            ReferenceId: request.TranslationRoomId,
            ReferenceType: TransactionConstants.ReferenceTypes.UsageRecord,
            TranslationRoomId: request.TranslationRoomId,
            TranscriptSegmentId: request.SegmentId,
            Quantity: request.Quantity,
            Unit: request.Unit,
            CreditsConsumed: request.CreditsConsumed,
            IdempotencyKey: BillingIdempotencyKeyHelper.ForUsage(request),
            PricingRateCardId: null,
            UnitPriceSnapshot: null,
            Currency: PaymentConstants.Currencies.VndAccounting,
            Details: request.Details);

    public static SettleUsageChargeRequest ToSettlementRequest(
        this ConsumeCreditsRequest request,
        Subscription subscription,
        Guid workspaceId)
        => new(
            SubscriptionId: subscription.Id,
            UserId: subscription.UserId,
            WorkspaceId: workspaceId,
            UsageType: CreditRatesHelper.GetUsageType(request.ReferenceType),
            ChargeType: request.ReferenceType,
            ReferenceId: request.ReferenceId,
            ReferenceType: request.ReferenceType,
            TranslationRoomId: request.ReferenceId,
            TranscriptSegmentId: null,
            Quantity: 1,
            Unit: UsageConstants.UsageUnits.Request,
            CreditsConsumed: request.Amount,
            IdempotencyKey: BillingIdempotencyKeyHelper.ForDirectConsume(workspaceId, request),
            PricingRateCardId: null,
            UnitPriceSnapshot: null,
            Currency: PaymentConstants.Currencies.VndAccounting,
            Details: null);

    public static SettleUsageChargeRequest ToAggregatedSettlementRequest(this IEnumerable<TempUsageLogDto> logs)
    {
        var items = logs.ToList();
        if (items.Count == 0)
            throw new ArgumentException("Aggregated usage logs cannot be empty.", nameof(logs));

        var first = items[0];
        var totalCredits = items.Sum(x => x.CreditsConsumed);
        var totalQuantity = items.Sum(x => x.Quantity);

        return new SettleUsageChargeRequest(
            SubscriptionId: first.SubscriptionId,
            UserId: ParseGuidOrNull(first.UserId),
            WorkspaceId: first.WorkspaceId,
            UsageType: first.UsageType,
            ChargeType: first.ChargeType,
            ReferenceId: first.ReferenceId,
            ReferenceType: first.ReferenceType,
            TranslationRoomId: ParseGuidOrNull(first.TranslationRoomId),
            TranscriptSegmentId: first.TranscriptSegmentId,
            Quantity: (decimal)totalQuantity,
            Unit: first.Unit,
            CreditsConsumed: totalCredits,
            IdempotencyKey: BillingIdempotencyKeyHelper.ForAggregate(items.Select(x => x.IdempotencyKey)),
            PricingRateCardId: first.PricingRateCardId,
            UnitPriceSnapshot: first.UnitPriceSnapshot,
            Currency: PaymentConstants.Currencies.VndAccounting,
            Details: JsonSerializer.Serialize(new
            {
                description = BillingMessageConstants.UsageMessages.AggregatedBatchDescription,
                chargeType = first.ChargeType,
                provider = first.Provider,
                model = first.Model,
                pricingRateCardId = first.PricingRateCardId,
                unitPriceSnapshot = first.UnitPriceSnapshot,
                sourceEventCount = items.Count,
                sourceIdempotencyKeys = items.Select(x => x.IdempotencyKey).OrderBy(x => x).ToArray()
            }));
    }

    private static Guid? ParseGuidOrNull(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;
}

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Helpers;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.Shared.Models;

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

        long totalMicroCredits = items.Sum(x => x.MicroCredits ?? (x.CreditsConsumed * UsageConstants.MicroCreditsPerCredit));
        int totalCreditsCeil = (int)Math.Ceiling((double)totalMicroCredits / UsageConstants.MicroCreditsPerCredit);

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
            CreditsConsumed: totalCreditsCeil,
            IdempotencyKey: BillingIdempotencyKeyHelper.ForAggregate(items.Select(x => x.IdempotencyKey)),
            PricingRateCardId: first.PricingRateCardId,
            UnitPriceSnapshot: first.UnitPriceSnapshot,
            Currency: PaymentConstants.Currencies.VndAccounting,
            Details: CreateAggregatedDetails(items, first));
    }

    private static Guid? ParseGuidOrNull(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;

    private static string CreateAggregatedDetails(IReadOnlyList<TempUsageLogDto> items, TempUsageLogDto first)
    {
        var unitBreakdown = items
            .SelectMany(ToUnitBreakdownItems)
            .GroupBy(item => new
            {
                Unit = item.Unit ?? string.Empty,
                item.PricingRateCardId,
                item.UnitPriceSnapshot,
                Provider = item.Provider ?? string.Empty,
                Model = item.Model ?? string.Empty
            })
            .Select(group => new
            {
                unit = group.Key.Unit,
                quantity = group.Sum(x => x.Quantity).ToString("0.######"),
                pricing_rate_card_id = group.Key.PricingRateCardId,
                unit_price_snapshot = group.Key.UnitPriceSnapshot?.ToString("0.######"),
                provider = string.IsNullOrWhiteSpace(group.Key.Provider) ? null : group.Key.Provider,
                model = string.IsNullOrWhiteSpace(group.Key.Model) ? null : group.Key.Model
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            description = BillingMessageConstants.UsageMessages.AggregatedBatchDescription,
            chargeType = first.ChargeType,
            provider = first.Provider,
            model = first.Model,
            pricingScope = first.PricingScope,
            sourceLanguageCode = first.SourceLanguageCode,
            targetLanguageCode = first.TargetLanguageCode,
            pricingRateCardId = first.PricingRateCardId,
            unitPriceSnapshot = first.UnitPriceSnapshot,
            sourceEventCount = items.Count,
            sourceIdempotencyKeys = items.Select(x => x.IdempotencyKey).OrderBy(x => x).ToArray(),
            unit_breakdown = unitBreakdown
        });
    }

    private static IEnumerable<UnitBreakdownItem> ToUnitBreakdownItems(TempUsageLogDto log)
    {
        var parsedItems = ParseUnitBreakdown(log.Details).ToList();
        if (parsedItems.Count > 0)
            return parsedItems;

        return new[]
        {
            new UnitBreakdownItem(
                log.Unit,
                (decimal)log.Quantity,
                log.PricingRateCardId,
                log.UnitPriceSnapshot,
                log.Provider,
                log.Model)
        };
    }

    private static IEnumerable<UnitBreakdownItem> ParseUnitBreakdown(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            yield break;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(details);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root?["unit_breakdown"] is not JsonArray breakdown)
            yield break;

        foreach (var item in breakdown)
        {
            if (item is null)
                continue;

            var unit = item["unit"]?.GetValue<string>();
            var quantityText = item["quantity"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(unit) || !decimal.TryParse(quantityText, out var quantity))
                continue;

            Guid? pricingRateCardId = null;
            var pricingRateCardIdText = item["pricing_rate_card_id"]?.GetValue<string>();
            if (Guid.TryParse(pricingRateCardIdText, out var parsedId))
                pricingRateCardId = parsedId;

            decimal? unitPriceSnapshot = null;
            var unitPriceSnapshotText = item["unit_price_snapshot"]?.GetValue<string>();
            if (decimal.TryParse(unitPriceSnapshotText, out var parsedPrice))
                unitPriceSnapshot = parsedPrice;

            yield return new UnitBreakdownItem(
                unit,
                quantity,
                pricingRateCardId,
                unitPriceSnapshot,
                item["provider"]?.GetValue<string>(),
                item["model"]?.GetValue<string>());
        }
    }

    private sealed record UnitBreakdownItem(
        string? Unit,
        decimal Quantity,
        Guid? PricingRateCardId,
        decimal? UnitPriceSnapshot,
        string? Provider,
        string? Model);
}

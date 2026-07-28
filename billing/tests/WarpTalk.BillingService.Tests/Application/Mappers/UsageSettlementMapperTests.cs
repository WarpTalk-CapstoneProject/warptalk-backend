using FluentAssertions;
using System.Text.Json;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Tests.Application.Mappers;

public class UsageSettlementMapperTests
{
    [Fact]
    public void RecordUsageRequest_ToSettlementRequest_Should_Map_Usage_Charge()
    {
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        var workspaceId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var request = new RecordUsageRequest(
            workspaceId,
            userId,
            UsageConstants.UsageTypes.AiAssistant,
            UsageConstants.UsageUnits.Token,
            123,
            10,
            null,
            roomId,
            segmentId,
            "{\"source\":\"test\"}");

        var settlement = request.ToSettlementRequest(subscription);

        settlement.SubscriptionId.Should().Be(subscription.Id);
        settlement.WorkspaceId.Should().Be(workspaceId);
        settlement.UserId.Should().Be(userId);
        settlement.ChargeType.Should().Be(UsageConstants.UsageTypes.AiAssistant);
        settlement.ReferenceType.Should().Be(TransactionConstants.ReferenceTypes.UsageRecord);
        settlement.TranslationRoomId.Should().Be(roomId);
        settlement.TranscriptSegmentId.Should().Be(segmentId);
        settlement.Currency.Should().Be(PaymentConstants.Currencies.VndAccounting);
        settlement.IdempotencyKey.Should().StartWith("USAGE:");
    }

    [Fact]
    public void TempUsageLogs_ToAggregatedSettlementRequest_Should_Preserve_Rate_Snapshot()
    {
        var pricingRateId = Guid.NewGuid();
        var logs = new[]
        {
            new TempUsageLogDto
            {
                SubscriptionId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                UsageType = "AI_ASSISTANT",
                ChargeType = "AI_ASSISTANT",
                ReferenceType = TransactionConstants.ReferenceTypes.AggregatedBatch,
                Quantity = 10,
                Unit = "token_out",
                CreditsConsumed = 2,
                PricingRateCardId = pricingRateId,
                UnitPriceSnapshot = 0.1m,
                Provider = "openai",
                Model = "gpt-4.1",
                IdempotencyKey = "event-1"
            },
            new TempUsageLogDto
            {
                SubscriptionId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                UsageType = "AI_ASSISTANT",
                ChargeType = "AI_ASSISTANT",
                ReferenceType = TransactionConstants.ReferenceTypes.AggregatedBatch,
                Quantity = 20,
                Unit = "token_out",
                CreditsConsumed = 3,
                PricingRateCardId = pricingRateId,
                UnitPriceSnapshot = 0.1m,
                Provider = "openai",
                Model = "gpt-4.1",
                IdempotencyKey = "event-2"
            }
        };

        var settlement = logs.ToAggregatedSettlementRequest();

        settlement.Quantity.Should().Be(30);
        settlement.CreditsConsumed.Should().Be(5);
        settlement.PricingRateCardId.Should().Be(pricingRateId);
        settlement.UnitPriceSnapshot.Should().Be(0.1m);
        settlement.Currency.Should().Be(PaymentConstants.Currencies.VndAccounting);
        settlement.IdempotencyKey.Should().StartWith("AGG:");
    }

    [Fact]
    public void TempUsageLogs_ToAggregatedSettlementRequest_Should_Merge_UnitBreakdown_Details()
    {
        var tokenInRateId = Guid.NewGuid();
        var tokenOutRateId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var logs = new[]
        {
            new TempUsageLogDto
            {
                SubscriptionId = subscriptionId,
                WorkspaceId = workspaceId,
                UsageType = "TRANSLATION",
                ChargeType = "TRANSLATION",
                ReferenceType = "billing_accumulator",
                Quantity = 100,
                Unit = "token_in",
                CreditsConsumed = 3,
                PricingScope = "vi:en",
                PricingRateCardId = tokenInRateId,
                UnitPriceSnapshot = 0.006575m,
                Provider = "openai",
                Model = "gpt-4.1-mini",
                IdempotencyKey = "event-1",
                Details = $$"""
                {
                  "unit_breakdown": [
                    {
                      "unit": "token_in",
                      "quantity": "100",
                      "pricing_rate_card_id": "{{tokenInRateId}}",
                      "unit_price_snapshot": "0.006575",
                      "provider": "openai",
                      "model": "gpt-4.1-mini"
                    },
                    {
                      "unit": "token_out",
                      "quantity": "20",
                      "pricing_rate_card_id": "{{tokenOutRateId}}",
                      "unit_price_snapshot": "0.026300",
                      "provider": "openai",
                      "model": "gpt-4.1-mini"
                    }
                  ]
                }
                """
            },
            new TempUsageLogDto
            {
                SubscriptionId = subscriptionId,
                WorkspaceId = workspaceId,
                UsageType = "TRANSLATION",
                ChargeType = "TRANSLATION",
                ReferenceType = "billing_accumulator",
                Quantity = 50,
                Unit = "token_in",
                CreditsConsumed = 2,
                PricingScope = "vi:en",
                PricingRateCardId = tokenInRateId,
                UnitPriceSnapshot = 0.006575m,
                Provider = "openai",
                Model = "gpt-4.1-mini",
                IdempotencyKey = "event-2"
            }
        };

        var settlement = logs.ToAggregatedSettlementRequest();

        using var details = JsonDocument.Parse(settlement.Details!);
        var breakdown = details.RootElement.GetProperty("unit_breakdown");
        breakdown.GetArrayLength().Should().Be(2);
        breakdown.EnumerateArray().Should().Contain(item =>
            item.GetProperty("unit").GetString() == "token_in" &&
            item.GetProperty("quantity").GetString() == "150");
        breakdown.EnumerateArray().Should().Contain(item =>
            item.GetProperty("unit").GetString() == "token_out" &&
            item.GetProperty("quantity").GetString() == "20");
    }
}

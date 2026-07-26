using FluentAssertions;
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
}

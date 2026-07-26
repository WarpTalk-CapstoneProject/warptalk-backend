using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Infrastructure.Workers;
using WarpTalk.Shared;

namespace WarpTalk.BillingService.Tests.Infrastructure.Workers;

public class BillingAggregationWorkerTests
{
    [Fact]
    public async Task AggregateTempLogsIntoUnitOfWorkAsync_ShouldPreserveRateSnapshotsPerModel()
    {
        var subscriptionId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var gpt41RateId = Guid.NewGuid();
        var gpt5RateId = Guid.NewGuid();
        var settlementRequests = new List<SettleUsageChargeRequest>();
        var settlementService = new Mock<IUsageSettlementService>();

        settlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SettleUsageChargeRequest, CancellationToken>((request, _) => settlementRequests.Add(request))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(
                true,
                Guid.NewGuid(),
                Guid.NewGuid(),
                100,
                SubscriptionConstants.ServiceStates.Healthy,
                null)));

        var logs = new List<TempUsageLogDto>
        {
            new()
            {
                SubscriptionId = subscriptionId,
                WorkspaceId = workspaceId,
                UsageType = "AI_ASSISTANT",
                ChargeType = "AI_ASSISTANT",
                Quantity = 100,
                Unit = "token_out",
                CreditsConsumed = 14,
                Provider = "openai",
                Model = "gpt-4.1",
                PricingRateCardId = gpt41RateId,
                UnitPriceSnapshot = 0.131500m,
                ReferenceType = "billing_accumulator",
                IdempotencyKey = "AI_ASSISTANT:token_out:openai:gpt-4.1:room:1",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                SubscriptionId = subscriptionId,
                WorkspaceId = workspaceId,
                UsageType = "AI_ASSISTANT",
                ChargeType = "AI_ASSISTANT",
                Quantity = 100,
                Unit = "token_out",
                CreditsConsumed = 3,
                Provider = "openai",
                Model = "gpt-5-mini",
                PricingRateCardId = gpt5RateId,
                UnitPriceSnapshot = 0.025000m,
                ReferenceType = "billing_accumulator",
                IdempotencyKey = "AI_ASSISTANT:token_out:openai:gpt-5-mini:room:1",
                CreatedAt = DateTime.UtcNow
            }
        };

        await BillingAggregationWorker.AggregateTempLogsAsync(
            logs,
            settlementService.Object,
            null,
            null,
            CancellationToken.None);

        settlementRequests.Should().HaveCount(2);
        settlementRequests.Should().Contain(t =>
            t.PricingRateCardId == gpt41RateId &&
            t.UnitPriceSnapshot == 0.131500m &&
            t.ChargeType == "AI_ASSISTANT" &&
            t.Currency == PaymentConstants.Currencies.VndAccounting);
        settlementRequests.Should().Contain(t =>
            t.PricingRateCardId == gpt5RateId &&
            t.UnitPriceSnapshot == 0.025000m &&
            t.ChargeType == "AI_ASSISTANT" &&
            t.Currency == PaymentConstants.Currencies.VndAccounting);
    }
}

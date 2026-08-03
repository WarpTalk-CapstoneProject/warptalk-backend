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
            null, null, null, null, CancellationToken.None);

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

    [Fact]
    public async Task AggregateTempLogsAsync_Should_Alert_When_Settlement_Fails()
    {
        var settlementService = new Mock<IUsageSettlementService>();
        settlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<SettleUsageChargeResult>("database unavailable", ErrorCodes.InternalServerError));

        var alertService = new Mock<IBillingOperationalAlertService>();
        alertService
            .Setup(a => a.AlertSettlementFailedAsync(It.IsAny<SettleUsageChargeRequest>(), "database unavailable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var logs = new List<TempUsageLogDto>
        {
            new()
            {
                SubscriptionId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                UsageType = "AI_ASSISTANT",
                ChargeType = "AI_ASSISTANT",
                Quantity = 100,
                Unit = "token_out",
                CreditsConsumed = 10,
                Provider = "openai",
                Model = "gpt-4.1",
                ReferenceType = "billing_accumulator",
                IdempotencyKey = "AI_ASSISTANT:token_out:openai:gpt-4.1:room:failure",
                CreatedAt = DateTime.UtcNow
            }
        };

        await BillingAggregationWorker.AggregateTempLogsAsync(
            logs,
            settlementService.Object,
            null,
            null,
            alertService.Object, null, null, null, CancellationToken.None);

        alertService.Verify(a => a.AlertSettlementFailedAsync(
            It.Is<SettleUsageChargeRequest>(r => r.ChargeType == "AI_ASSISTANT"),
            "database unavailable",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 1)] // 0.001 -> 1
    [InlineData(500000, 1)] // 0.5 -> 1
    [InlineData(1000000, 1)] // 1.0 -> 1
    [InlineData(1499900, 2)] // 1.4999 -> 2
    public async Task AggregateTempLogsAsync_Should_Ceil_Accumulator_Correctly_For_MicroCredits(long microCredits, int expectedCredits)
    {
        var settlementRequests = new List<SettleUsageChargeRequest>();
        var settlementService = new Mock<IUsageSettlementService>();

        settlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SettleUsageChargeRequest, CancellationToken>((request, _) => settlementRequests.Add(request))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(true, Guid.NewGuid(), Guid.NewGuid(), 100, null, null)));

        var logs = new List<TempUsageLogDto>
        {
            new()
            {
                SubscriptionId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                UsageType = "test",
                ChargeType = "test",
                Quantity = 1,
                Unit = "request",
                MicroCredits = microCredits,
                ReferenceType = "test",
                IdempotencyKey = "test:1",
                CreatedAt = DateTime.UtcNow
            }
        };

        await BillingAggregationWorker.AggregateTempLogsAsync(logs, settlementService.Object, null, null, null, null, null, null, CancellationToken.None);

        if (expectedCredits > 0)
        {
            settlementRequests.Should().HaveCount(1);
            settlementRequests.Single().CreditsConsumed.Should().Be(expectedCredits);
        }
        else
        {
            settlementRequests.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task AggregateTempLogsAsync_Should_Accumulate_Fractional_Events_And_Ceil()
    {
        var settlementRequests = new List<SettleUsageChargeRequest>();
        var settlementService = new Mock<IUsageSettlementService>();

        settlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SettleUsageChargeRequest, CancellationToken>((request, _) => settlementRequests.Add(request))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(true, Guid.NewGuid(), Guid.NewGuid(), 100, null, null)));

        var subscriptionId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid(); // Same workspace for all events so they aggregate
        // 3 events of 1.36 credits each = 1,360,000 microcredits
        var logs = Enumerable.Range(0, 3).Select(i => new TempUsageLogDto
        {
            SubscriptionId = subscriptionId,
            WorkspaceId = workspaceId,
            UsageType = "test",
            ChargeType = "test",
            Quantity = 1,
            Unit = "minute",
            MicroCredits = 1360000,
            ReferenceType = "test",
            IdempotencyKey = $"test:{i}",
            CreatedAt = DateTime.UtcNow
        }).ToList();

        await BillingAggregationWorker.AggregateTempLogsAsync(logs, settlementService.Object, null, null, null, null, null, null, CancellationToken.None);

        // 1.36 * 3 = 4.08 -> ceil(4.08) = 5
        settlementRequests.Should().HaveCount(1);
        settlementRequests.Single().CreditsConsumed.Should().Be(5);
    }

    [Fact]
    public async Task AggregateTempLogsAsync_Should_Block_And_Log_Error_When_MaxCreditsPerFlush_Exceeded()
    {
        var settlementRequests = new List<SettleUsageChargeRequest>();
        var settlementService = new Mock<IUsageSettlementService>();

        settlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SettleUsageChargeRequest, CancellationToken>((request, _) => settlementRequests.Add(request))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(true, Guid.NewGuid(), Guid.NewGuid(), 100, null, null)));

        var logs = new List<TempUsageLogDto>
        {
            new()
            {
                SubscriptionId = Guid.NewGuid(),
                WorkspaceId = Guid.NewGuid(),
                UsageType = "test",
                ChargeType = "test",
                Quantity = 1,
                Unit = "request",
                MicroCredits = 15_000 * UsageConstants.MicroCreditsPerCredit, // Exceeds 10,000
                ReferenceType = "test",
                IdempotencyKey = "test:1",
                CreatedAt = DateTime.UtcNow
            }
        };

        await BillingAggregationWorker.AggregateTempLogsAsync(logs, settlementService.Object, null, null, null, null, null, null, CancellationToken.None);

        settlementRequests.Should().BeEmpty(); // Should not charge
    }

    [Fact]
    public async Task AggregateTempLogsAsync_Should_Deduplicate_AiAssistant_TokenIn_Events()
    {
        var settlementRequests = new List<SettleUsageChargeRequest>();
        var settlementService = new Mock<IUsageSettlementService>();

        settlementService
            .Setup(s => s.SettleUsageChargeAsync(It.IsAny<SettleUsageChargeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SettleUsageChargeRequest, CancellationToken>((request, _) => settlementRequests.Add(request))
            .ReturnsAsync(Result.Success(new SettleUsageChargeResult(true, Guid.NewGuid(), Guid.NewGuid(), 100, null, null)));

        var subscriptionId = Guid.NewGuid();
        var referenceId = Guid.NewGuid();

        // 3 loops with overlapping input tokens
        var logs = new List<TempUsageLogDto>
        {
            new() { SubscriptionId = subscriptionId, WorkspaceId = Guid.NewGuid(), UsageType = UsageConstants.UsageTypes.AiAssistant, ChargeType = "ai", Unit = "token_in", ReferenceId = referenceId, ReferenceType = "test", Quantity = 1000, MicroCredits = 1000, IdempotencyKey = "1" },
            new() { SubscriptionId = subscriptionId, WorkspaceId = Guid.NewGuid(), UsageType = UsageConstants.UsageTypes.AiAssistant, ChargeType = "ai", Unit = "token_in", ReferenceId = referenceId, ReferenceType = "test", Quantity = 1050, MicroCredits = 1050, IdempotencyKey = "2" },
            new() { SubscriptionId = subscriptionId, WorkspaceId = Guid.NewGuid(), UsageType = UsageConstants.UsageTypes.AiAssistant, ChargeType = "ai", Unit = "token_in", ReferenceId = referenceId, ReferenceType = "test", Quantity = 1090, MicroCredits = 1090, IdempotencyKey = "3" }
        };

        await BillingAggregationWorker.AggregateTempLogsAsync(logs, settlementService.Object, null, null, null, null, null, null, CancellationToken.None);

        // Should only charge for the max one (1090)
        settlementRequests.Should().HaveCount(1);
        settlementRequests.Single().Quantity.Should().Be(1090);
        settlementRequests.Single().CreditsConsumed.Should().Be(1); // ceil(1090 / 1_000_000)
    }
}

using FluentAssertions;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Workers;

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
        var usageRecords = new List<UsageRecord>();
        var transactions = new List<CreditTransaction>();
        var usageRepo = new Mock<IGenericRepository<UsageRecord>>();
        var transactionRepo = new Mock<ICreditTransactionRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();

        usageRepo
            .Setup(r => r.AddAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()))
            .Callback<UsageRecord, CancellationToken>((record, _) => usageRecords.Add(record))
            .Returns(Task.CompletedTask);
        transactionRepo
            .Setup(r => r.AddAsync(It.IsAny<CreditTransaction>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransaction, CancellationToken>((transaction, _) => transactions.Add(transaction))
            .Returns(Task.CompletedTask);
        unitOfWork.Setup(u => u.UsageRecordRepository).Returns(usageRepo.Object);
        unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(transactionRepo.Object);

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

        await BillingAggregationWorker.AggregateTempLogsIntoUnitOfWorkAsync(
            logs,
            unitOfWork.Object,
            CancellationToken.None);

        usageRecords.Should().HaveCount(2);
        transactions.Should().HaveCount(2);
        transactions.Should().Contain(t =>
            t.PricingRateCardId == gpt41RateId &&
            t.UnitPriceSnapshot == 0.131500m &&
            t.UsageRecordId.HasValue &&
            t.ChargeType == "AI_ASSISTANT" &&
            t.Currency == "VND");
        transactions.Should().Contain(t =>
            t.PricingRateCardId == gpt5RateId &&
            t.UnitPriceSnapshot == 0.025000m &&
            t.UsageRecordId.HasValue &&
            t.ChargeType == "AI_ASSISTANT" &&
            t.Currency == "VND");
    }
}

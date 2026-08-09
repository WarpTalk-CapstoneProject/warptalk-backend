using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Services;
using WarpTalk.BillingService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.BillingService.Tests.Integration;

public class UsageSettlementIntegrationTests : BaseIntegrationTest
{
    private PostgresUsageSettlementService _settlementService = null!;
    private BillingDbContext _db = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        
        var scope = ServiceProvider.CreateScope();
        _db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        // Same DbContext instance the test writes its fixtures through, so the
        // settlement command sees those rows and shares their connection.
        var repository = new UsageSettlementRepository(_db);
        _settlementService = new PostgresUsageSettlementService(
            repository,
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PostgresUsageSettlementService>>(),
            new Moq.Mock<IBillingMessagePublisher>().Object);
    }

    private async Task<Subscription> CreateTestSubscriptionAsync(
        int creditsRemaining = 0,
        int overageCapCredits = 0,
        int lowBalanceThresholdCredits = 0)
    {
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Test Plan",
            Slug = $"test-{Guid.NewGuid()}",
            Tier = SubscriptionConstants.Tiers.Enterprise,
            Price = 0,
            CreditsPerCycle = 1000,
            OverageCapCredits = overageCapCredits,
            LowBalanceThresholdCredits = overageCapCredits > 0
                ? Math.Max(lowBalanceThresholdCredits, overageCapCredits + 1)
                : lowBalanceThresholdCredits,
            BillingCycle = "monthly"
        };
        _db.Plans.Add(plan);

        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Status = "active",
            CreditsRemaining = creditsRemaining,
            CreditsUsedThisCycle = 0,
            CurrentPeriodStart = DateTime.UtcNow.AddDays(-1),
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
            ServiceState = "healthy",
            OverageCreditsThisCycle = Math.Max(0, -creditsRemaining)
        };
        _db.Subscriptions.Add(sub);
        
        await _db.SaveChangesAsync();
        return sub;
    }

    [DockerFact]
    public async Task SettleUsage_ChargeWithinOverageLimit_ShouldSucceed()
    {
        // Arrange
        // CreditsRemaining = -5, OverageCap = 10
        // We charge 5 more, which hits exact limit (credits = -10, equal to -OverageCap)
        var sub = await CreateTestSubscriptionAsync(creditsRemaining: -5, overageCapCredits: 10);
        var req = CreateRequest(sub, 5);

        // Act
        var result = await _settlementService.SettleUsageChargeAsync(req);

        // Assert
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Applied.Should().BeTrue();
        result.Value.BalanceAfter.Should().Be(-10);
        result.Value.ServiceState.Should().Be("suspended"); // Assuming it hits the cap and gets suspended
        result.Value.SuspendedReason.Should().Be("overage_cap");
    }

    [DockerFact]
    public async Task SettleUsage_ChargeExceedsOverageLimit_ShouldFailAndNotCharge()
    {
        // Arrange
        // CreditsRemaining = -5, OverageCap = 10
        // We charge 6 more, which exceeds limit (credits = -11 < -10)
        var sub = await CreateTestSubscriptionAsync(creditsRemaining: -5, overageCapCredits: 10);
        var req = CreateRequest(sub, 6);

        // Act
        var result = await _settlementService.SettleUsageChargeAsync(req);

        // Assert
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Applied.Should().BeFalse();
        
        // Ensure no transaction was recorded
        var txCount = await _db.CreditTransactions.CountAsync(x => x.SubscriptionId == sub.Id);
        txCount.Should().Be(0);

        // Ensure balance is untouched
        var updatedSub = await _db.Subscriptions.FirstAsync(s => s.Id == sub.Id);
        updatedSub.CreditsRemaining.Should().Be(-5);
    }

    [DockerFact]
    public async Task SettleUsage_OverageCapZero_Trial_ShouldBlockAtZero()
    {
        // Arrange
        var sub = await CreateTestSubscriptionAsync(creditsRemaining: 3, overageCapCredits: 0);
        var req1 = CreateRequest(sub, 3, "key1"); // Charge exactly 3
        var req2 = CreateRequest(sub, 1, "key2"); // Try to charge 1 more

        // Act
        var res1 = await _settlementService.SettleUsageChargeAsync(req1);
        var res2 = await _settlementService.SettleUsageChargeAsync(req2);

        // Assert
        res1.IsSuccess.Should().BeTrue(res1.Error);
        res2.IsSuccess.Should().BeTrue(res2.Error);
        res1.Value!.Applied.Should().BeTrue();
        res1.Value.BalanceAfter.Should().Be(0);
        res1.Value.ServiceState.Should().Be("low_balance");

        res2.Value!.Applied.Should().BeFalse(); // Cannot go below 0
    }

    [DockerFact]
    public async Task SettleUsage_ConcurrentCharges_SumExceedsLimit_OnlyOneSucceeds()
    {
        // Arrange
        var sub = await CreateTestSubscriptionAsync(creditsRemaining: 0, overageCapCredits: 50);
        
        // Two requests trying to consume 30 each. Total 60 exceeds 50 cap.
        var req1 = CreateRequest(sub, 30, "c-key1");
        var req2 = CreateRequest(sub, 30, "c-key2");

        // Act
        // Execute concurrently
        using var scope1 = ServiceProvider.CreateScope();
        using var scope2 = ServiceProvider.CreateScope();
        var settlementService1 = scope1.ServiceProvider.GetRequiredService<IUsageSettlementService>();
        var settlementService2 = scope2.ServiceProvider.GetRequiredService<IUsageSettlementService>();

        var t1 = settlementService1.SettleUsageChargeAsync(req1);
        var t2 = settlementService2.SettleUsageChargeAsync(req2);
        var results = await Task.WhenAll(t1, t2);

        // Assert
        var successCount = results.Count(r => r.IsSuccess && r.Value!.Applied);
        var failureCount = results.Count(r => r.IsSuccess && !r.Value!.Applied);

        successCount.Should().Be(1);
        failureCount.Should().Be(1);

        var updatedSub = await _db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == sub.Id);
        updatedSub.CreditsRemaining.Should().Be(-30);
    }

    [DockerFact]
    public async Task SettleUsage_OverageCreditsThisCycle_UpdatesCorrectly_All3Cases()
    {
        // Arrange
        var sub = await CreateTestSubscriptionAsync(creditsRemaining: 10, overageCapCredits: 100);
        
        // Case 1: still positive (+0 overage)
        var req1 = CreateRequest(sub, 4, "k1");
        var res1 = await _settlementService.SettleUsageChargeAsync(req1);
        res1.IsSuccess.Should().BeTrue(res1.Error);
        res1.Value!.Applied.Should().BeTrue();
        
        var subAfter1 = await _db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == sub.Id);
        subAfter1.OverageCreditsThisCycle.Should().Be(0);
        subAfter1.CreditsRemaining.Should().Be(6);

        // Case 2: crosses zero (only part exceeding zero added)
        var req2 = CreateRequest(sub, 10, "k2");
        var res2 = await _settlementService.SettleUsageChargeAsync(req2);
        res2.IsSuccess.Should().BeTrue(res2.Error);
        res2.Value!.Applied.Should().BeTrue();

        var subAfter2 = await _db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == sub.Id);
        subAfter2.OverageCreditsThisCycle.Should().Be(4); // 6 - 10 = -4 -> 4 overage
        subAfter2.CreditsRemaining.Should().Be(-4);

        // Case 3: already negative (all added)
        var req3 = CreateRequest(sub, 5, "k3");
        var res3 = await _settlementService.SettleUsageChargeAsync(req3);
        res3.IsSuccess.Should().BeTrue(res3.Error);
        res3.Value!.Applied.Should().BeTrue();

        var subAfter3 = await _db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == sub.Id);
        subAfter3.OverageCreditsThisCycle.Should().Be(9); // 4 + 5 = 9
        subAfter3.CreditsRemaining.Should().Be(-9);
    }

    [DockerFact]
    public async Task SettleUsage_ServiceStateTransitions()
    {
        // Arrange
        // Start healthy
        var sub = await CreateTestSubscriptionAsync(creditsRemaining: 20, lowBalanceThresholdCredits: 11, overageCapCredits: 10);

        // Healthy -> Low Balance
        var req1 = CreateRequest(sub, 15, "state-1");
        var res1 = await _settlementService.SettleUsageChargeAsync(req1);
        res1.IsSuccess.Should().BeTrue(res1.Error);
        res1.Value!.ServiceState.Should().Be("low_balance");

        // Low Balance -> In Overage
        var req2 = CreateRequest(sub, 10, "state-2");
        var res2 = await _settlementService.SettleUsageChargeAsync(req2);
        res2.IsSuccess.Should().BeTrue(res2.Error);
        res2.Value!.ServiceState.Should().Be("in_overage");

        var updatedSub = await _db.Subscriptions.AsNoTracking().FirstAsync(s => s.Id == sub.Id);
        updatedSub.OverageStartedAt.Should().NotBeNull();

        // In Overage -> Suspended
        var req3 = CreateRequest(sub, 5, "state-3");
        var res3 = await _settlementService.SettleUsageChargeAsync(req3);
        res3.IsSuccess.Should().BeTrue(res3.Error);
        res3.Value!.ServiceState.Should().Be("suspended");
        res3.Value.SuspendedReason.Should().Be("overage_cap");
    }

    private SettleUsageChargeRequest CreateRequest(Subscription sub, int credits, string? key = null)
    {
        return new SettleUsageChargeRequest(
            SubscriptionId: sub.Id,
            UserId: sub.UserId,
            WorkspaceId: sub.WorkspaceId,
            UsageType: "STT",
            ChargeType: "pay_as_you_go",
            ReferenceId: Guid.NewGuid(),
            ReferenceType: "translation_room",
            TranslationRoomId: Guid.NewGuid(),
            TranscriptSegmentId: null,
            Quantity: 10,
            Unit: "second",
            CreditsConsumed: credits,
            IdempotencyKey: key ?? Guid.NewGuid().ToString(),
            PricingRateCardId: Guid.NewGuid(),
            UnitPriceSnapshot: 0.1m,
            Currency: "VND",
            Details: "{}"
        );
    }
}

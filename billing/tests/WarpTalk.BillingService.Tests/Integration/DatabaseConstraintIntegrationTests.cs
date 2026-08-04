using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;
using Xunit;

namespace WarpTalk.BillingService.Tests.Integration;

public class DatabaseConstraintIntegrationTests : BaseIntegrationTest
{
    private BillingDbContext _db = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        
        var scope = ServiceProvider.CreateScope();
        _db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    }

    [DockerFact]
    public async Task PriceFloorConstraint_ShouldReject_WhenPricePerCreditIsBelow260()
    {
        // Arrange
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise Test",
            Slug = $"ent-{Guid.NewGuid()}",
            Tier = SubscriptionConstants.Tiers.Enterprise,
            Price = 1900000m,
            CreditsPerCycle = 700000,
            BillingCycle = "monthly"
        };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();

        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Status = "active",
            CreditsRemaining = 1000,
            // 2.5 VND per credit < 2.60
            ContractPriceVnd = 250000m,
            CreditsPerCycleOverride = 100000
        };
        _db.Subscriptions.Add(sub);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        exception.InnerException.Should().NotBeNull();
        exception.InnerException!.Message.Should().Contain("subscriptions_price_floor_chk");
    }

    [DockerFact]
    public async Task PriceFloorConstraint_ShouldAccept_WhenPricePerCreditIsExactly260()
    {
        // Arrange
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise Test",
            Slug = $"ent-{Guid.NewGuid()}",
            Tier = SubscriptionConstants.Tiers.Enterprise,
            Price = 1900000m,
            CreditsPerCycle = 700000,
            BillingCycle = "monthly"
        };
        _db.Plans.Add(plan);
        await _db.SaveChangesAsync();

        var sub = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Status = "active",
            CreditsRemaining = 1000,
            // Exactly at the configured price floor
            ContractPriceVnd = 260000m,
            CreditsPerCycleOverride = 100000
        };
        _db.Subscriptions.Add(sub);

        // Act
        var result = await _db.SaveChangesAsync();

        // Assert
        result.Should().BeGreaterThan(0);
    }

    [DockerFact]
    public async Task OverageThresholdConstraint_ShouldReject_WhenLowBalanceThresholdIsLessThanOverageCap()
    {
        // Arrange
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Enterprise Test",
            Slug = $"ent-{Guid.NewGuid()}",
            Tier = SubscriptionConstants.Tiers.Enterprise,
            Price = 1900000m,
            CreditsPerCycle = 700000,
            BillingCycle = "monthly",
            OverageCapCredits = 200000,
            // Invalid: Low Balance threshold (100k) < Overage Cap (200k)
            LowBalanceThresholdCredits = 100000
        };
        _db.Plans.Add(plan);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        exception.InnerException.Should().NotBeNull();
        exception.InnerException!.Message.Should().Contain("plans_warn_before_overage_chk");
    }

    [DockerFact]
    public async Task ResolveContractTerms_ShouldPreferOverridesOverPlanDefaults()
    {
        // Arrange
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Test Plan",
            Slug = $"test-{Guid.NewGuid()}",
            Tier = SubscriptionConstants.Tiers.Enterprise,
            Price = 100m,
            CreditsPerCycle = 1000,
            OverageCapCredits = 100,
            OveragePricePerCredit = 5m,
            LowBalanceThresholdCredits = 150,
            InvoiceTermsDays = 15,
            BillingCycle = "monthly"
        };
        _db.Plans.Add(plan);

        var subWithOverrides = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Status = "active",
            CreditsRemaining = 1000,
            ContractPriceVnd = 5200m,
            CreditsPerCycleOverride = 2000,
            OverageCapCreditsOverride = 200,
            OveragePricePerCreditOverride = 4m,
            InvoiceTermsDaysOverride = 30
        };
        _db.Subscriptions.Add(subWithOverrides);
        
        var subWithoutOverrides = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PlanId = plan.Id,
            Status = "active",
            CreditsRemaining = 1000
        };
        _db.Subscriptions.Add(subWithoutOverrides);

        await _db.SaveChangesAsync();

        // Act - Call SQL Function using Dapper or ExecuteSqlRaw directly, or mapping
        // We will just read it via ADO.NET since it returns a TABLE
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var cmdWith = connection.CreateCommand();
        cmdWith.CommandText = "SELECT credits_per_cycle, contract_price_vnd, overage_cap_credits, overage_price_per_credit, invoice_terms_days FROM subscription.resolve_contract_terms(@id)";
        var pWith = cmdWith.CreateParameter();
        pWith.ParameterName = "id";
        pWith.Value = subWithOverrides.Id;
        cmdWith.Parameters.Add(pWith);
        
        await using var readerWith = await cmdWith.ExecuteReaderAsync();
        await readerWith.ReadAsync();
        var creditsOverride = readerWith.GetInt32(0);
        var priceOverride = readerWith.GetDecimal(1);
        var overageCapOverride = readerWith.GetInt32(2);
        var overagePriceOverride = readerWith.GetDecimal(3);
        var invoiceTermsOverride = readerWith.GetInt32(4);
        await readerWith.DisposeAsync();

        // Assert Overrides
        creditsOverride.Should().Be(2000);
        priceOverride.Should().Be(5200m);
        overageCapOverride.Should().Be(200);
        overagePriceOverride.Should().Be(4m);
        invoiceTermsOverride.Should().Be(30);

        // Act - Without overrides
        await using var cmdWithout = connection.CreateCommand();
        cmdWithout.CommandText = "SELECT credits_per_cycle, contract_price_vnd, overage_cap_credits, overage_price_per_credit, invoice_terms_days FROM subscription.resolve_contract_terms(@id)";
        var pWithout = cmdWithout.CreateParameter();
        pWithout.ParameterName = "id";
        pWithout.Value = subWithoutOverrides.Id;
        cmdWithout.Parameters.Add(pWithout);

        await using var readerWithout = await cmdWithout.ExecuteReaderAsync();
        await readerWithout.ReadAsync();
        var creditsDef = readerWithout.GetInt32(0);
        var priceDef = readerWithout.IsDBNull(1) ? (decimal?)null : readerWithout.GetDecimal(1);
        var overageCapDef = readerWithout.GetInt32(2);
        var overagePriceDef = readerWithout.GetDecimal(3);
        var invoiceTermsDef = readerWithout.GetInt32(4);

        // Assert Defaults fall back to Plan
        creditsDef.Should().Be(1000); // From Plan
        priceDef.Should().BeNull(); // No override
        overageCapDef.Should().Be(100); // From Plan
        overagePriceDef.Should().Be(5m); // From Plan
        invoiceTermsDef.Should().Be(15); // From Plan
    }
}

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.BillingService.UnitTests;

public class QuotaIntegrationTests
{
    private DbContextOptions<BillingDbContext> CreateNewContextOptions()
    {
        return new DbContextOptionsBuilder<BillingDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
    }

    [Fact]
    public async Task DeductQuota_FullFlow_Success()
    {
        // Arrange
        var options = CreateNewContextOptions();
        using var context = new BillingDbContext(options);
        
        var workspaceId = Guid.NewGuid();
        var plan = new SubscriptionPlan
        {
            Id = Guid.NewGuid(),
            Name = WarpTalk.BillingService.Domain.Enums.PlanType.Pro,
            BaseQuotaMinutes = 100,
            PriceVnd = 200000,
            MaxParticipants = 10
        };
        context.SubscriptionPlans.Add(plan);

        context.UsageQuotas.Add(new UsageQuota 
        { 
            WorkspaceId = workspaceId, 
            PlanId = plan.Id,
            TotalAllocatedMinutes = 100,
            ConsumedMinutes = 0,
            CycleStartDate = DateTime.UtcNow,
            CycleEndDate = DateTime.UtcNow.AddMonths(1)
        });
        await context.SaveChangesAsync();

        var quotaRepo = new UsageQuotaRepository(context);
        var auditRepo = new QuotaAuditLogRepository(context);
        var planRepo = new SubscriptionPlanRepository(context);
        var logger = new Mock<ILogger<QuotaService>>().Object;
        
        var quotaService = new QuotaService(quotaRepo, auditRepo, planRepo, context, logger);
        
        var sessionId = Guid.NewGuid();
        var request = new QuotaDeductRequest(sessionId, 15.5m, "Meeting 1");

        // Act
        var result = await quotaService.DeductQuotaAsync(workspaceId, request);

        // Assert
        result.Success.Should().BeTrue(because: $"Deduction should succeed but failed with reason: {result.Reason}");
        result.RemainingMinutes.Should().Be(84.5m);

        var dbQuota = await context.UsageQuotas.FirstAsync(q => q.WorkspaceId == workspaceId);
        dbQuota.ConsumedMinutes.Should().Be(15.5m);

        var auditLog = await context.QuotaAuditLogs.FirstOrDefaultAsync();
        auditLog.Should().NotBeNull();
        auditLog!.Amount.Should().Be(15.5m);
        auditLog.BalanceAfter.Should().Be(84.5m);
    }
}

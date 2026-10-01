using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Tests.Infrastructure;
using WarpTalk.Shared.AdminAudit;
using WarpTalk.Shared.Events;
using Xunit;

namespace WarpTalk.BillingService.Tests.Integration;

/// <summary>
/// Against real PostgreSQL: an audited save that the audit log refuses does not reach the table,
/// and one it accepts does — the ordering "record, then commit, or do not commit".
/// </summary>
public class AdminAuditedSaveIntegrationTests : BaseIntegrationTest
{
    private (BillingDbContext Context, RecordingAuditSink Sink) ArmedContext()
    {
        using var scope = Factory.Services.CreateScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.GetConnectionString();

        var sink = new RecordingAuditSink();
        var auditScope = new AdminAuditScope();
        auditScope.Arm(new AdminAuditInvocation
        {
            Action = AdminAuditBillingActions.PlanUpdated,
            EntityType = AdminAuditEntityTypes.Plan,
            SubjectTypes = [typeof(Plan)],
            ActorId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString("N"),
        });
        var services = new ServiceCollection()
            .AddSingleton(auditScope)
            .AddSingleton<IAdminAuditSink>(sink)
            .BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = services } };

        var context = new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new AdminAuditSaveChangesInterceptor(accessor))
            .Options);
        return (context, sink);
    }

    private async Task<Guid> SeedPlanAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Audit Pro",
            Slug = $"audit-{Guid.NewGuid():N}",
            Tier = "pro",
            Price = 499000m,
            CreditsPerCycle = 50000,
            BillingCycle = "monthly",
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    [DockerFact]
    public async Task A_refused_audit_leaves_the_row_as_it_was()
    {
        var planId = await SeedPlanAsync();
        var (context, sink) = ArmedContext();
        sink.Refuse = true;
        await using (context)
        {
            var plan = await context.Plans.SingleAsync(p => p.Id == planId);
            plan.Price = 1m;

            await Assert.ThrowsAsync<AdminAuditRefusedException>(() => context.SaveChangesAsync());
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Assert.Equal(499000m, (await db.Plans.AsNoTracking().SingleAsync(p => p.Id == planId)).Price);
    }

    [DockerFact]
    public async Task An_accepted_audit_commits_and_records_the_diff()
    {
        var planId = await SeedPlanAsync();
        var (context, sink) = ArmedContext();
        await using (context)
        {
            var plan = await context.Plans.SingleAsync(p => p.Id == planId);
            plan.Price = 599000m;
            await context.SaveChangesAsync();
        }

        var record = Assert.Single(sink.Records);
        Assert.Equal(planId, record.EntityId);
        Assert.Equal("499000", record.BeforeSummary!["price"]);
        Assert.Equal("599000", record.AfterSummary!["price"]);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        Assert.Equal(599000m, (await db.Plans.AsNoTracking().SingleAsync(p => p.Id == planId)).Price);
    }
}

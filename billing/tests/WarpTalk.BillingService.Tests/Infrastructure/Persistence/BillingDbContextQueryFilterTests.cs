using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Infrastructure.Persistence.Contexts;
using Xunit;

namespace WarpTalk.BillingService.Tests.Infrastructure.Persistence;

public sealed class BillingDbContextQueryFilterTests
{
    [Fact]
    public void SubscriptionDependents_ShouldHaveFiltersThatRespectSoftDeletedSubscriptions()
    {
        using var context = new BillingDbContext(
            new DbContextOptionsBuilder<BillingDbContext>()
                .UseNpgsql("Host=localhost;Database=test;Username=test;Password=test")
                .Options);

        var dependentTypes = new[]
        {
            typeof(CreditTransaction),
            typeof(CreditBalanceSnapshot),
            typeof(UsageRecord),
            typeof(Payment),
            typeof(Transaction),
            typeof(Invoice),
            typeof(Refund)
        };

        foreach (var dependentType in dependentTypes)
        {
            var filterDefinition = context.Model.FindEntityType(dependentType)?
                .GetDeclaredQueryFilters()
                .SingleOrDefault();

            filterDefinition.Should().NotBeNull($"{dependentType.Name} must not expose soft-deleted subscriptions through required navigations");
            var expression = filterDefinition?.Expression;
            expression.Should().NotBeNull();
            expression!.ToString().Should().Contain("DeletedAt");
        }
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;

namespace WarpTalk.BillingService.Tests.Infrastructure.Repositories;

/// <summary>The admin Providers page's workspace split must be SQL PostgreSQL can be asked (see ProfitAndLossQueryTranslationTests).</summary>
public sealed class ProviderQueryTranslationTests
{
    [Fact]
    public void Provider_workspace_consumption_translates_to_one_grouped_query()
    {
        var repository = new CreditTransactionRepository(new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options));

        var sql = repository.ProviderWorkspaceQuery(
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc)).ToQueryString();

        sql.Should().Contain("GROUP BY").And.Contain("workspace_id").And.Contain("usage_rate_card");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;

namespace WarpTalk.BillingService.Tests.Infrastructure.Repositories;

/// <summary>
/// The profit-and-loss slot queries must be SQL PostgreSQL can be asked — the failure mode that shipped a
/// 500 before ("EF cannot order a positional-record projection") is a translation error, and ToQueryString
/// raises it without a database. The Docker-backed service tests run the same queries against real rows.
/// </summary>
public sealed class ProfitAndLossQueryTranslationTests
{
    private static CreditTransactionRepository Repository()
        => new(new BillingDbContext(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options));

    private static readonly DateTime From = new(2026, 8, 31, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Consumption_slots_translate_to_one_grouped_query()
    {
        var sql = Repository().ConsumptionSlotsQuery(From, To).ToQueryString();

        sql.Should().Contain("GROUP BY").And.Contain("usage_rate_card").And.Contain("subscriptions");
        sql.Should().Contain("date_part('hour'").And.Contain("date_part('minute'");
    }

    [Fact]
    public void Workspace_slots_translate_to_one_grouped_query()
    {
        var sql = Repository().WorkspaceSlotsQuery(From, To).ToQueryString();

        sql.Should().Contain("GROUP BY").And.Contain("workspace_id");
    }
}

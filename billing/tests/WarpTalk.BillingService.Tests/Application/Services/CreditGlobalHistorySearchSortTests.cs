using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Interfaces;
using WarpTalk.BillingService.Application.Services;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.BillingService.Infrastructure.Persistence;
using WarpTalk.BillingService.Infrastructure.Repositories;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.BillingService.Tests.Application.Services;

/// <summary>
/// Search and sort on GET api/v1/credits/history/global, and proof that the workspace-scoped
/// history — which shares the helper and the repository method — is unchanged.
/// </summary>
public class CreditGlobalHistorySearchSortTests
{
    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ISubscriptionRepository> _subscriptions = new();
    private readonly Mock<ICreditTransactionRepository> _transactions = new();
    private readonly CreditService _service;
    private CreditTransactionHistoryFilter? _seen;

    public CreditGlobalHistorySearchSortTests()
    {
        _unitOfWork.Setup(u => u.SubscriptionRepository).Returns(_subscriptions.Object);
        _unitOfWork.Setup(u => u.CreditTransactionRepository).Returns(_transactions.Object);
        _transactions
            .Setup(r => r.GetHistoryPageAsync(It.IsAny<CreditTransactionHistoryFilter>(), It.IsAny<CancellationToken>()))
            .Callback<CreditTransactionHistoryFilter, CancellationToken>((f, _) => _seen = f)
            .ReturnsAsync(new PagedResult<CreditTransaction>(Array.Empty<CreditTransaction>(), 0, 1, 20));

        _service = new CreditService(
            _unitOfWork.Object,
            Mock.Of<ILogger<CreditService>>(),
            Mock.Of<IUsageSettlementService>(),
            Mock.Of<IWorkspaceClient>());
    }

    // ── Service ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_sort_is_a_validation_error()
    {
        var result = await _service.GetGlobalCreditHistoryAsync(new GlobalCreditHistoryQuery { Sort = "balance_desc" });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _seen.Should().BeNull();
    }

    [Fact]
    public async Task Search_and_sort_reach_the_repository()
    {
        var result = await _service.GetGlobalCreditHistoryAsync(
            new GlobalCreditHistoryQuery { Search = "  dubbing  ", Sort = "AMOUNT_DESC" });

        result.IsSuccess.Should().BeTrue(result.Error);
        _seen!.Search.Should().Be("dubbing");
        _seen.Sort.Should().Be(CreditHistorySorts.AmountDesc);
    }

    [Fact]
    public async Task The_global_default_is_the_old_filter()
    {
        await _service.GetGlobalCreditHistoryAsync(new GlobalCreditHistoryQuery());

        _seen.Should().Be(new CreditTransactionHistoryFilter(new PageRequest(1, 20)));
        _seen!.Sort.Should().Be(CreditHistorySorts.CreatedDesc);
    }

    [Fact]
    public async Task The_workspace_history_never_searches_and_keeps_created_desc()
    {
        var workspaceId = Guid.NewGuid();
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspaceId };
        _subscriptions
            .Setup(r => r.FindAsync(It.IsAny<Expression<Func<Subscription, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Subscription> { subscription });

        var result = await _service.GetCreditHistoryAsync(workspaceId, new CreditHistoryQuery(Type: "debit"));

        result.IsSuccess.Should().BeTrue(result.Error);
        _seen!.Search.Should().BeNull();
        _seen.Sort.Should().Be(CreditHistorySorts.CreatedDesc);
        _seen.SubscriptionIds.Should().Equal(subscription.Id);
    }

    // ── Repository over plain rows ───────────────────────────

    private static CreditTransaction Tx(int amount, DateTime at, string? description = null, Guid? referenceId = null) => new()
    {
        Id = Guid.NewGuid(),
        Amount = amount,
        CreatedAt = at,
        Description = description,
        ReferenceId = referenceId,
        Type = amount < 0 ? "debit" : "credit",
    };

    private static readonly Guid MeetingRef = Guid.NewGuid();
    private static readonly CreditTransaction Small = Tx(-10, Anchor, "Dubbing en->vi", MeetingRef);
    private static readonly CreditTransaction TopUp = Tx(5000, Anchor.AddDays(1), "Top-up");
    private static readonly CreditTransaction Large = Tx(-800, Anchor.AddDays(2));

    private static List<Guid> Filter(string? search) =>
        CreditTransactionRepository
            .ApplyHistoryFilters(new[] { Small, TopUp, Large }.AsQueryable(), new CreditTransactionHistoryFilter(new PageRequest(1, 20), Search: search))
            .Select(t => t.Id)
            .ToList();

    private static List<Guid> Sort(string sort) =>
        CreditTransactionRepository.ApplyHistorySort(new[] { Small, TopUp, Large }.AsQueryable(), sort)
            .Select(t => t.Id)
            .ToList();

    [Fact]
    public void A_guid_matches_the_transaction_id_or_its_reference_id()
    {
        Filter(TopUp.Id.ToString()).Should().Equal(TopUp.Id);
        Filter(MeetingRef.ToString()).Should().Equal(Small.Id);
        Filter(Guid.NewGuid().ToString()).Should().BeEmpty();
        Filter(null).Should().HaveCount(3);
    }

    [Fact]
    public void Each_sort_orders_the_rows()
    {
        Sort(CreditHistorySorts.CreatedDesc).Should().Equal(Large.Id, TopUp.Id, Small.Id);
        Sort(CreditHistorySorts.CreatedAsc).Should().Equal(Small.Id, TopUp.Id, Large.Id);
        // Magnitude, like minAmount/maxAmount: the 800-credit debit outranks the 10-credit one.
        Sort(CreditHistorySorts.AmountDesc).Should().Equal(TopUp.Id, Large.Id, Small.Id);
        Sort(CreditHistorySorts.AmountAsc).Should().Equal(Small.Id, Large.Id, TopUp.Id);
    }

    // ── Translation ──────────────────────────────────────────

    private static BillingDbContext Context() =>
        new(new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);

    [Fact]
    public void Text_search_is_an_ilike_on_description_and_amount_sort_is_on_abs()
    {
        using var context = Context();
        var filter = new CreditTransactionHistoryFilter(new PageRequest(1, 20), Search: "dub");

        var sql = CreditTransactionRepository
            .ApplyHistorySort(CreditTransactionRepository.ApplyHistoryFilters(context.CreditTransactions, filter), CreditHistorySorts.AmountDesc)
            .ToQueryString();

        sql.Should().Contain("description ILIKE @").And.MatchRegex(@"ORDER BY abs\(\w+\.amount\) DESC");
    }

    [Fact]
    public void The_default_order_is_exactly_the_old_one()
    {
        using var context = Context();
        var source = CreditTransactionRepository.ApplyHistoryFilters(
            context.CreditTransactions, new CreditTransactionHistoryFilter(new PageRequest(1, 20)));

        CreditTransactionRepository.ApplyHistorySort(source, CreditHistorySorts.CreatedDesc).ToQueryString()
            .Should().Be(source.OrderByDescending(t => t.CreatedAt).ToQueryString());
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.TranscriptService.Application.DTOs;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// The sort parameter on GET api/v1/admin/global-glossary. The ordering the service hands the
/// repository is captured and run over plain rows, so each key is checked for what it actually
/// orders by — including that omitting it keeps the historical priority-then-newest order.
/// </summary>
public class GlobalGlossaryTermListSortTests
{
    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IGlobalGlossaryTermRepository _terms = Substitute.For<IGlobalGlossaryTermRepository>();
    private readonly GlobalGlossaryService _service;
    private Func<IQueryable<GlobalGlossaryTerm>, IOrderedQueryable<GlobalGlossaryTerm>>? _orderBy;

    public GlobalGlossaryTermListSortTests()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.GlobalGlossaryTerms.Returns(_terms);
        _terms
            .GetPagedAsync(
                Arg.Any<Expression<Func<GlobalGlossaryTerm, bool>>>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Do<Func<IQueryable<GlobalGlossaryTerm>, IOrderedQueryable<GlobalGlossaryTerm>>?>(o => _orderBy = o),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GlobalGlossaryTerm>());

        _service = new GlobalGlossaryService(
            unitOfWork,
            Substitute.For<ILogger<GlobalGlossaryService>>(),
            Substitute.For<IConnectionMultiplexer>());
    }

    private static GlobalGlossaryTerm Term(string term, int priority, DateTime createdAt, DateTime updatedAt) => new()
    {
        Id = Guid.NewGuid(),
        Term = term,
        PreferredTranslation = term,
        Priority = priority,
        CreatedAt = createdAt,
        UpdatedAt = updatedAt,
    };

    // Priority ties between Beta and Gamma so the historical order has to fall back to created_at.
    private static readonly GlobalGlossaryTerm Alpha = Term("alpha", 9, Anchor, Anchor.AddDays(1));
    private static readonly GlobalGlossaryTerm Beta = Term("beta", 5, Anchor.AddDays(2), Anchor.AddDays(9));
    private static readonly GlobalGlossaryTerm Gamma = Term("gamma", 5, Anchor.AddDays(4), Anchor.AddDays(5));

    private async Task<List<string>> OrderedBy(string? sort)
    {
        var result = await _service.GetTermsAsync(new GlobalGlossaryTermQuery(Sort: sort));
        Assert.True(result.IsSuccess, result.Error);
        Assert.NotNull(_orderBy);
        return _orderBy!(new[] { Alpha, Beta, Gamma }.AsQueryable()).Select(t => t.Term).ToList();
    }

    [Fact]
    public async Task Omitting_sort_keeps_priority_then_newest()
    {
        Assert.Equal(new[] { "alpha", "gamma", "beta" }, await OrderedBy(null));
        Assert.Equal(new[] { "alpha", "gamma", "beta" }, await OrderedBy("priority_desc"));
    }

    [Theory]
    [InlineData("updated_desc", new[] { "beta", "gamma", "alpha" })]
    [InlineData("created_desc", new[] { "gamma", "beta", "alpha" })]
    [InlineData("term_asc", new[] { "alpha", "beta", "gamma" })]
    [InlineData("TERM_DESC", new[] { "gamma", "beta", "alpha" })]
    public async Task Each_sort_orders_by_its_key(string sort, string[] expected)
    {
        Assert.Equal(expected, await OrderedBy(sort));
    }

    [Fact]
    public async Task An_unknown_sort_is_a_validation_error_and_reads_nothing()
    {
        var result = await _service.GetTermsAsync(new GlobalGlossaryTermQuery(Sort: "usage_desc"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        await _terms.DidNotReceiveWithAnyArgs().GetPagedAsync(default!, default, default, default, default);
    }
}

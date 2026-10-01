using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs.Admin;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// search / minRating / maxRating on GET api/v1/admin/feedback/comments: validated, forwarded to
/// the comment query only (the summary must not move), and translatable by Npgsql.
/// Infrastructure/AdminFeedbackAggregationTests runs them against real PostgreSQL.
/// </summary>
public class AdminFeedbackCommentFilterTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITranslationRoomFeedbackRepository> _feedback = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly AdminFeedbackService _service;
    private AdminFeedbackCommentCriteria? _criteria;
    private bool _commentsRead;

    public AdminFeedbackCommentFilterTests()
    {
        _unitOfWork.SetupGet(u => u.TranslationRoomFeedbackRepository).Returns(_feedback.Object);
        _feedback
            .Setup(r => r.GetAdminCommentsAsync(
                It.IsAny<AdminFeedbackFilter>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<AdminFeedbackCommentCriteria?>()))
            .Callback<AdminFeedbackFilter, int, int, CancellationToken, bool, AdminFeedbackCommentCriteria?>(
                (_, _, _, _, _, c) => { _commentsRead = true; _criteria = c; })
            .ReturnsAsync(((IReadOnlyList<AdminFeedbackCommentRow>)Array.Empty<AdminFeedbackCommentRow>(), 0));
        _feedback
            .Setup(r => r.GetAdminStatsAsync(It.IsAny<AdminFeedbackFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new AdminFeedbackTotals(0, 0, 0), (IReadOnlyList<AdminFeedbackDimensionStats>)Array.Empty<AdminFeedbackDimensionStats>()));

        _service = new AdminFeedbackService(
            _unitOfWork.Object,
            new FixedTime(Now),
            NullLogger<AdminFeedbackService>.Instance);
    }

    private sealed class FixedTime(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(6, null)]
    [InlineData(null, 0)]
    [InlineData(null, 6)]
    [InlineData(4, 2)]
    public async Task An_out_of_scale_or_inverted_rating_range_is_a_validation_error(int? min, int? max)
    {
        var result = await _service.GetCommentsAsync(new AdminFeedbackQuery { MinRating = min, MaxRating = max });

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _commentsRead.Should().BeFalse();
    }

    [Fact]
    public async Task Search_and_rating_range_reach_the_comment_query()
    {
        var result = await _service.GetCommentsAsync(
            new AdminFeedbackQuery { Search = "  lag  ", MinRating = 1, MaxRating = 2, Sort = "lowest" });

        result.IsSuccess.Should().BeTrue(result.Error);
        _criteria.Should().Be(new AdminFeedbackCommentCriteria("lag", 1, 2));
    }

    [Fact]
    public async Task An_equal_min_and_max_is_one_rating_not_an_error()
    {
        var result = await _service.GetCommentsAsync(new AdminFeedbackQuery { MinRating = 5, MaxRating = 5 });

        result.IsSuccess.Should().BeTrue(result.Error);
        _criteria.Should().Be(new AdminFeedbackCommentCriteria(null, 5, 5));
    }

    [Fact]
    public async Task No_new_parameter_means_no_narrowing()
    {
        await _service.GetCommentsAsync(new AdminFeedbackQuery());

        _criteria.Should().Be(new AdminFeedbackCommentCriteria());
    }

    [Fact]
    public async Task The_summary_ignores_the_comment_filters_even_when_invalid()
    {
        var result = await _service.GetSummaryAsync(
            new AdminFeedbackQuery { Search = "lag", MinRating = 9, MaxRating = 1 });

        result.IsSuccess.Should().BeTrue(result.Error);
        _feedback.Verify(
            r => r.GetAdminStatsAsync(It.IsAny<AdminFeedbackFilter>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void The_comment_query_translates_with_ilike_and_the_rating_bounds()
    {
        using var context = new TranslationRoomDbContext(new DbContextOptionsBuilder<TranslationRoomDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options);
        var repository = new TranslationRoomFeedbackRepository(context);

        var sql = repository
            .AdminCommentsQuery(new AdminFeedbackFilter(Now.AddDays(-30), Now), new AdminFeedbackCommentCriteria("lag", 1, 3))
            .ToQueryString();

        sql.Should().Contain("comments ILIKE @")
            .And.Contain("title ILIKE @")
            .And.Contain("overall_rating >= @")
            .And.Contain("overall_rating <= @");

        var plain = repository.AdminCommentsQuery(new AdminFeedbackFilter(Now.AddDays(-30), Now)).ToQueryString();
        plain.Should().NotContain("ILIKE").And.NotContain("overall_rating >=");
    }
}

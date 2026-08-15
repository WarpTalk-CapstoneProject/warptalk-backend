using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// Real PostgreSQL. This aggregates five nullable columns with GROUP BY and joins across to the
/// room for the workspace filter — the shape that has already shipped from this codebase passing
/// against mocks and returning 500 on every real call.
/// </summary>
public sealed class AdminFeedbackAggregationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly AdminFeedbackFilter Window =
        new(Anchor.AddDays(-1), Anchor.AddDays(30));

    private readonly Guid _workspaceA = Guid.NewGuid();
    private readonly Guid _workspaceB = Guid.NewGuid();

    private readonly Guid _roomA1 = Guid.NewGuid();
    private readonly Guid _roomA2 = Guid.NewGuid();
    private readonly Guid _roomB1 = Guid.NewGuid();
    private readonly Guid _roomDeleted = Guid.NewGuid();
    private readonly Guid _roomUnrated = Guid.NewGuid();

    private TranslationRoomDbContext _context = null!;
    private TranslationRoomFeedbackRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new TranslationRoomDbContext(
            new DbContextOptionsBuilder<TranslationRoomDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

        // postgres:16 has no uuidv7() builtin, and the schema defaults to it.
        await _context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION uuidv7() RETURNS uuid AS $$
            DECLARE
                timestamp_ms bigint;
                timestamp_hex text;
                uuid_hex text;
            BEGIN
                timestamp_ms := (extract(epoch from clock_timestamp()) * 1000)::bigint;
                timestamp_hex := lpad(to_hex(timestamp_ms), 12, '0');
                uuid_hex := timestamp_hex || '7' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || '8' || lpad(to_hex((random() * 4095)::integer), 3, '0')
                    || lpad(to_hex((random() * 281474976710655)::bigint), 12, '0');
                RETURN uuid_hex::uuid;
            END;
            $$ LANGUAGE plpgsql;
            """);
        await _context.Database.EnsureCreatedAsync();

        _repository = new TranslationRoomFeedbackRepository(_context);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        _context.TranslationRooms.AddRange(
            Room(_roomA1, _workspaceA, endedAt: Anchor.AddDays(1)),
            Room(_roomA2, _workspaceA, endedAt: Anchor.AddDays(2)),
            Room(_roomB1, _workspaceB, endedAt: Anchor.AddDays(3)),
            // Ended and nobody rated it. Present so the response rate has a real denominator.
            Room(_roomUnrated, _workspaceA, endedAt: Anchor.AddDays(4)),
            Room(_roomDeleted, _workspaceA, endedAt: Anchor.AddDays(5), deletedAt: Anchor.AddDays(6)));

        _context.TranslationRoomFeedbacks.AddRange(
            // Workspace A. Every dimension answered.
            Feedback(_roomA1, overall: 5, translation: 5, audio: 4, clone: 3, summary: 5,
                comment: "The dub kept up with the speaker.", at: Anchor.AddDays(1)),
            // Overall only — the four optional dimensions left blank, which is the normal case.
            Feedback(_roomA1, overall: 1, at: Anchor.AddDays(1).AddHours(1)),
            Feedback(_roomA2, overall: 3, translation: 3, audio: 3,
                comment: "  ", at: Anchor.AddDays(2)),
            // Workspace B.
            Feedback(_roomB1, overall: 5, translation: 4, comment: "Good.", at: Anchor.AddDays(3)),
            // On a soft-deleted room: counted nowhere, because the row it names cannot be opened.
            Feedback(_roomDeleted, overall: 1, translation: 1, comment: "Broken.",
                at: Anchor.AddDays(5)),
            // Outside the window entirely.
            Feedback(_roomA1, overall: 1, at: Anchor.AddDays(90)));

        await _context.SaveChangesAsync();
    }

    private static TranslationRoom Room(
        Guid id,
        Guid workspaceId,
        DateTime endedAt,
        DateTime? deletedAt = null) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        HostId = Guid.NewGuid(),
        Title = $"Room {id.ToString("N")[..4]}",
        TranslationRoomCode = $"WARP-{id.ToString("N")[..6]}",
        Status = nameof(RoomStatus.ENDED),
        TranslationRoomType = "STANDARD",
        MaxParticipants = 10,
        SourceLanguage = "vi",
        // JSONB, not a delimited string.
        TargetLanguages = "[\"en\"]",
        Settings = "{}",
        StartedAt = endedAt.AddMinutes(-30),
        EndedAt = endedAt,
        DurationSeconds = 1800,
        IsActive = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
        DeletedAt = deletedAt,
    };

    private static TranslationRoomFeedback Feedback(
        Guid roomId,
        int overall,
        int? translation = null,
        int? audio = null,
        int? clone = null,
        int? summary = null,
        string? comment = null,
        DateTime at = default) => new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = roomId,
        UserId = Guid.NewGuid(),
        OverallRating = overall,
        TranslationQuality = translation,
        AudioQuality = audio,
        VoiceCloneQuality = clone,
        AiSummaryQuality = summary,
        Comments = comment,
        CreatedAt = at == default ? Anchor : at,
    };

    [Fact]
    public async Task The_aggregation_translates_to_SQL_at_all()
    {
        var act = async () => await _repository.GetAdminStatsAsync(Window);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_optional_dimension_averages_over_its_own_respondents()
    {
        // Four people rated overall, three rated translation quality. Dividing translation's sum
        // by four would report 3.0 where the answer is 4.0 — an average silently diluted by
        // people who declined to answer.
        var (totals, dimensions) = await _repository.GetAdminStatsAsync(Window);

        totals.ResponseCount.Should().Be(4);

        var overall = dimensions.Single(d => d.Dimension == "overallRating");
        overall.ResponseCount.Should().Be(4);
        overall.AverageRating.Should().Be(3.5); // (5 + 1 + 3 + 5) / 4

        var translation = dimensions.Single(d => d.Dimension == "translationQuality");
        translation.ResponseCount.Should().Be(3);
        translation.AverageRating.Should().Be(4.0); // (5 + 3 + 4) / 3
    }

    [Fact]
    public async Task A_dimension_nobody_rated_is_null_not_zero()
    {
        // Zero out of five is the worst score there is. "Nobody answered" is not a bad score.
        var (_, dimensions) = await _repository.GetAdminStatsAsync(Window);

        var summary = dimensions.Single(d => d.Dimension == "aiSummaryQuality");
        summary.ResponseCount.Should().Be(1);

        var clone = dimensions.Single(d => d.Dimension == "voiceCloneQuality");
        clone.ResponseCount.Should().Be(1);
        clone.AverageRating.Should().Be(3.0);
    }

    [Fact]
    public async Task The_distribution_separates_two_identical_averages()
    {
        // Overall averages 3.5 here from 5, 1, 3, 5 — nothing like a room full of people who
        // thought it was fine. The mean alone cannot tell those apart; the buckets can.
        var (_, dimensions) = await _repository.GetAdminStatsAsync(Window);

        var overall = dimensions.Single(d => d.Dimension == "overallRating");
        overall.Distribution.Should().Equal([1, 0, 1, 0, 2]);
    }

    [Fact]
    public async Task Ended_meetings_are_the_denominator_and_exclude_deleted_ones()
    {
        var (totals, _) = await _repository.GetAdminStatsAsync(Window);

        // Three rated rooms out of four that ended and still exist.
        totals.RoomsWithFeedback.Should().Be(3);
        totals.EndedRooms.Should().Be(4);
    }

    [Fact]
    public async Task Feedback_on_a_soft_deleted_room_is_counted_nowhere()
    {
        var (totals, dimensions) = await _repository.GetAdminStatsAsync(Window);
        var (comments, commentTotal) = await _repository.GetAdminCommentsAsync(Window, 1, 20);

        totals.ResponseCount.Should().Be(4);
        dimensions.Single(d => d.Dimension == "overallRating").Distribution[0].Should().Be(1);
        commentTotal.Should().Be(2);
        comments.Should().NotContain(c => c.Comment == "Broken.");
    }

    [Fact]
    public async Task The_window_is_measured_on_when_the_rating_was_given()
    {
        // The out-of-window rating sits on a room INSIDE the window. Filtering on the meeting's
        // date instead would pull it in, and the report would answer a question nobody asked.
        var (totals, _) = await _repository.GetAdminStatsAsync(
            new AdminFeedbackFilter(Anchor.AddDays(-1), Anchor.AddDays(30)));

        totals.ResponseCount.Should().Be(4);

        var (wider, _) = await _repository.GetAdminStatsAsync(
            new AdminFeedbackFilter(Anchor.AddDays(-1), Anchor.AddDays(120)));

        wider.ResponseCount.Should().Be(5);
    }

    [Fact]
    public async Task A_workspace_filter_scopes_both_the_ratings_and_the_denominator()
    {
        var (totals, dimensions) = await _repository.GetAdminStatsAsync(
            Window with { WorkspaceId = _workspaceB });

        totals.ResponseCount.Should().Be(1);
        totals.RoomsWithFeedback.Should().Be(1);
        totals.EndedRooms.Should().Be(1);
        dimensions.Single(d => d.Dimension == "overallRating").AverageRating.Should().Be(5.0);
    }

    [Fact]
    public async Task Comments_come_back_newest_first_and_skip_the_blank_ones()
    {
        // "  " is not a comment. It is also not an empty string, which is why the filter cannot
        // be a null-or-empty test alone.
        var (comments, total) = await _repository.GetAdminCommentsAsync(Window, 1, 20);

        total.Should().Be(2);
        comments[0].Comment.Should().Be("Good.");
        comments[1].Comment.Should().Be("The dub kept up with the speaker.");
        comments.Should().NotContain(c => string.IsNullOrWhiteSpace(c.Comment));
    }

    [Fact]
    public async Task A_comment_carries_its_room_and_workspace_but_no_person()
    {
        var (comments, _) = await _repository.GetAdminCommentsAsync(Window, 1, 20);

        var newest = comments[0];
        newest.TranslationRoomId.Should().Be(_roomB1);
        newest.WorkspaceId.Should().Be(_workspaceB);
        newest.OverallRating.Should().Be(5);
        newest.RoomTitle.Should().NotBeNullOrWhiteSpace();

        // The row type has no user id at all — a rating is feedback about the product, and
        // attaching a person to it makes a record about that person instead.
        typeof(AdminFeedbackCommentRow)
            .GetProperties()
            .Select(p => p.Name)
            .Should()
            .NotContain("UserId");
    }
}

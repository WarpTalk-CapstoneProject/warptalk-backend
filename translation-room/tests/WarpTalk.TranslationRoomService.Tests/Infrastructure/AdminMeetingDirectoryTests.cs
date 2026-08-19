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
/// Real PostgreSQL. The directory filters on a COALESCE across three nullable timestamps and
/// sorts on the same expression, which no in-memory provider evaluates the way Postgres does —
/// and billing has already shipped an admin aggregation that passed against mocks and returned
/// 500 on every real call.
/// </summary>
public sealed class AdminMeetingDirectoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private static readonly DateTime Anchor = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Midnight UTC today — the earliest instant that still counts as "today" for
    /// <c>GetAdminCountsAsync(DateTime.UtcNow.Date)</c>, whose boundary is inclusive (`>= since`).
    /// Never in the future, never yesterday, whatever time the suite runs.
    /// </summary>
    private static DateTime StartedToday => DateTime.UtcNow.Date;

    private readonly Guid _workspaceA = Guid.NewGuid();
    private readonly Guid _workspaceB = Guid.NewGuid();

    private readonly Guid _liveId = Guid.NewGuid();
    private readonly Guid _pausedId = Guid.NewGuid();
    private readonly Guid _waitingId = Guid.NewGuid();
    private readonly Guid _endedId = Guid.NewGuid();
    private readonly Guid _scheduledId = Guid.NewGuid();
    private readonly Guid _deletedId = Guid.NewGuid();

    private TranslationRoomDbContext _context = null!;
    private TranslationRoomRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _context = new TranslationRoomDbContext(
            new DbContextOptionsBuilder<TranslationRoomDbContext>()
                .UseNpgsql(_database.GetConnectionString())
                .Options);

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

        _repository = new TranslationRoomRepository(_context);
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
            // Started today, still running.
            //
            // Anchored to the START of today rather than to `UtcNow` minus a few minutes. The
            // subtraction made this a time bomb: `Counts_report_live_now_and_started_since_separately`
            // asks for rooms started since `UtcNow.Date`, so for the first twenty minutes after
            // midnight UTC the "20 minutes ago" room landed on YESTERDAY and the count came back 1
            // instead of 2. The suite was red every day between 00:00 and 00:20 UTC and green the
            // other 23h40m, which is exactly the shape that gets rerun until it passes and never
            // investigated. It blocked three unrelated PRs the night it was noticed.
            //
            // Midnight-today is always both today and in the past, so the boundary cannot move
            // underneath it. `StartedToday` keeps the two rooms distinguishable in time without
            // reintroducing the dependency on what o'clock it happens to be.
            Room(_liveId, _workspaceA, nameof(RoomStatus.IN_PROGRESS),
                startedAt: StartedToday),
            // Translation stopped, call did not. Still live.
            Room(_pausedId, _workspaceA, nameof(RoomStatus.PAUSED),
                startedAt: StartedToday.AddMinutes(1)),
            // Created, not opened. NOT live — nobody is in it.
            Room(_waitingId, _workspaceB, nameof(RoomStatus.WAITING),
                scheduledAt: Anchor.AddDays(3)),
            Room(_endedId, _workspaceA, nameof(RoomStatus.ENDED),
                startedAt: Anchor.AddDays(1), endedAt: Anchor.AddDays(1).AddMinutes(16),
                durationSeconds: 960),
            // Booked for a future day, never started.
            Room(_scheduledId, _workspaceB, nameof(RoomStatus.SCHEDULED),
                scheduledAt: Anchor.AddDays(10)),
            Room(_deletedId, _workspaceA, nameof(RoomStatus.ENDED),
                startedAt: Anchor.AddDays(2), deletedAt: Anchor.AddDays(2)));

        await _context.SaveChangesAsync();
    }

    private static TranslationRoom Room(
        Guid id,
        Guid workspaceId,
        string status,
        DateTime? scheduledAt = null,
        DateTime? startedAt = null,
        DateTime? endedAt = null,
        int? durationSeconds = null,
        DateTime? deletedAt = null) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        HostId = Guid.NewGuid(),
        Title = $"Room {status}",
        TranslationRoomCode = $"WARP-{id.ToString("N")[..6]}",
        Status = status,
        TranslationRoomType = "STANDARD",
        MaxParticipants = 10,
        SourceLanguage = "vi",
        // JSONB, not a delimited string — the column that made this test fail first time round.
        TargetLanguages = "[\"en\",\"ja\"]",
        Settings = "{}",
        ScheduledAt = scheduledAt,
        StartedAt = startedAt,
        EndedAt = endedAt,
        DurationSeconds = durationSeconds,
        IsActive = true,
        CreatedAt = Anchor,
        UpdatedAt = Anchor,
        DeletedAt = deletedAt,
    };

    [Fact]
    public async Task The_directory_query_translates_to_SQL_at_all()
    {
        var act = async () => await _repository.GetAdminDirectoryAsync(new AdminMeetingFilter(), 1, 20);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Soft_deleted_rooms_are_never_listed()
    {
        var (items, total) = await _repository.GetAdminDirectoryAsync(new AdminMeetingFilter(), 1, 20);

        total.Should().Be(5);
        items.Should().NotContain(r => r.Id == _deletedId);
    }

    [Fact]
    public async Task Live_spans_in_progress_and_paused_but_not_waiting()
    {
        // PAUSED means translation stopped, not the call. A count that dropped it would report an
        // in-progress meeting as finished. WAITING is the opposite case: created, nobody in it.
        var (items, total) = await _repository.GetAdminDirectoryAsync(
            new AdminMeetingFilter(Status: "live"), 1, 20);

        total.Should().Be(2);
        items.Select(r => r.Id).Should().BeEquivalentTo(new[] { _liveId, _pausedId });
    }

    [Fact]
    public async Task A_single_status_filters_to_exactly_that_status()
    {
        var (items, _) = await _repository.GetAdminDirectoryAsync(
            new AdminMeetingFilter(Status: nameof(RoomStatus.ENDED)), 1, 20);

        items.Should().ContainSingle().Which.Id.Should().Be(_endedId);
    }

    [Fact]
    public async Task Filtering_by_workspace_narrows_to_that_tenant()
    {
        var (_, total) = await _repository.GetAdminDirectoryAsync(
            new AdminMeetingFilter(WorkspaceId: _workspaceB), 1, 20);

        total.Should().Be(2);
    }

    [Fact]
    public async Task The_window_is_measured_against_when_the_meeting_happened()
    {
        // A room booked for day 10 and never started belongs to day 10, not to the day its row was
        // created — every row here was created on the anchor. Filtering on CreatedAt would put all
        // five in the same window, which is the confusion the workspace list already had to fix.
        var (items, _) = await _repository.GetAdminDirectoryAsync(
            new AdminMeetingFilter(From: Anchor.AddDays(5)), 1, 20);

        items.Select(r => r.Id).Should().Contain(_scheduledId);
        items.Select(r => r.Id).Should().NotContain(_endedId);
    }

    [Fact]
    public async Task Sorting_defaults_to_the_most_recent_activity_first()
    {
        var (items, _) = await _repository.GetAdminDirectoryAsync(new AdminMeetingFilter(), 1, 20);

        // The two running now started minutes ago, so they lead in either order; the room that
        // ended back on the anchor is last.
        new[] { _liveId, _pausedId }.Should().Contain(items[0].Id);
        items.Last().Id.Should().Be(_endedId);
    }

    [Fact]
    public async Task Counts_report_live_now_and_started_since_separately()
    {
        var (live, startedSince) = await _repository.GetAdminCountsAsync(DateTime.UtcNow.Date);

        live.Should().Be(2);
        // Only the two started today. The ENDED room started on the anchor, weeks earlier.
        startedSince.Should().Be(2);
    }

    [Fact]
    public async Task Counts_ignore_soft_deleted_rooms()
    {
        var (_, startedSince) = await _repository.GetAdminCountsAsync(Anchor.AddDays(-1));

        // Four rooms have a StartedAt; the deleted one is not among the counted.
        startedSince.Should().Be(3);
    }

    [Fact]
    public async Task Paging_reports_the_filtered_total()
    {
        var (items, total) = await _repository.GetAdminDirectoryAsync(new AdminMeetingFilter(), 1, 2);

        items.Should().HaveCount(2);
        total.Should().Be(5);
    }
}

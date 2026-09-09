using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// Revising approved minutes, against a real PostgreSQL.
///
/// The defect these were written for: <c>ReviseAsync</c> deliberately carries the number forward
/// (a revision of BB-2026-0007 is still BB-2026-0007), but the schema declared
/// <c>meeting_minutes_workspace_no_idx</c> unique on (workspace_id, minutes_no) with no filter —
/// so the insert of v2 hit 23505 and the feature could never complete a single call. No
/// mock-backed test could have seen it: the constraint is the thing under test, and it exists
/// only in the database.
/// </summary>
public sealed class MeetingMinutesRevisionTests
    : IClassFixture<MeetingMinutesRevisionTests.Database>, IAsyncLifetime
{
    /// <summary>
    /// One container for the whole class. xUnit builds a fresh test-class instance per fact, so a
    /// container owned by the instance is a container per fact — seven Postgres starts for this
    /// file alone, which is how the first run of these tests failed: not on an assertion, on
    /// Docker timing out under the pile.
    ///
    /// Tests share the database and stay independent by each using their own workspace and rooms
    /// (see <see cref="_workspaceId"/>), which is also what keeps the per-workspace numbering
    /// assertions from counting somebody else's minutes.
    /// </summary>
    public sealed class Database : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();

        public string ConnectionString => _container.GetConnectionString();

        public async Task InitializeAsync()
        {
            await _container.StartAsync();

            await using var context = new TranslationRoomDbContext(
                new DbContextOptionsBuilder<TranslationRoomDbContext>()
                    .UseNpgsql(_container.GetConnectionString())
                    .Options);

            await context.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
            await context.Database.ExecuteSqlRawAsync(
                "CREATE OR REPLACE FUNCTION public.uuidv7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
            await context.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => _container.DisposeAsync().AsTask();
    }

    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Database _database;

    /// <summary>Per test, so one test's minutes numbers are invisible to the next one's count.</summary>
    private readonly Guid _workspaceId = Guid.NewGuid();

    private TranslationRoomDbContext _context = null!;
    private MeetingMinutesService _service = null!;
    private MeetingMinutesRepository _minutes = null!;

    public MeetingMinutesRevisionTests(Database database) => _database = database;

    public Task InitializeAsync()
    {
        _context = new TranslationRoomDbContext(
            new DbContextOptionsBuilder<TranslationRoomDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .Options);

        _minutes = new MeetingMinutesRepository(_context);

        var unitOfWork = new UnitOfWork(
            _context,
            new TranslationRoomRepository(_context),
            new TranslationRoomParticipantRepository(_context),
            new TranslationRoomAudioRouteRepository(_context),
            new LanguageRepository(_context),
            new TranslationRoomArtifactRepository(_context),
            new TranslationRoomSessionRepository(_context),
            new TranslationRoomInvitationRepository(_context),
            new TranslationRoomFeedbackRepository(_context),
            new TranslationRoomSeriesRepository(_context),
            _minutes,
            new MeetingActionItemRepository(_context));

        _service = new MeetingMinutesService(
            unitOfWork,
            // Never consulted: every room here is hosted by the caller, and RoomHostAccess checks
            // host identity before it asks the directory anything.
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<IMeetingMinutesDocumentWriter>().Object,
            new Mock<ILogger<MeetingMinutesService>>().Object);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task<TranslationRoom> SeedEndedRoomAsync(string title = "Board meeting")
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = _workspaceId,
            HostId = HostId,
            Title = title,
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
            // Minutes can only be drawn up once the meeting has finished.
            Status = "ENDED",
            TranslationRoomType = "STANDARD",
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            IsActive = true,
            StartedAt = now.AddHours(-1),
            EndedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.TranslationRooms.Add(room);
        await _context.SaveChangesAsync();
        return room;
    }

    /// <summary>
    /// An APPROVED minutes of record, seeded directly rather than driven through
    /// draft/sign/approve: what is under test is the revision, and going the long way round would
    /// make a failure in any earlier step read as a failure here.
    /// </summary>
    private async Task<MeetingMinutes> SeedApprovedMinutesAsync(
        TranslationRoom room, string minutesNo, DateTime? createdAt = null)
    {
        var now = DateTime.UtcNow;
        var minutes = new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room.Id,
            WorkspaceId = room.WorkspaceId,
            MinutesNo = minutesNo,
            Status = MeetingMinutesConstants.StatusApproved,
            Version = 1,
            IsCurrent = true,
            SecretarySignedAt = now.AddMinutes(-10),
            ChairApprovedAt = now.AddMinutes(-5),
            EditCountVsDraft = 3,
            Content = "{\"sections\":[]}",
            CreatedAt = createdAt ?? now.AddMinutes(-20),
            CreatedBy = HostId,
            UpdatedAt = now.AddMinutes(-5),
            UpdatedBy = HostId
        };

        _context.MeetingMinutes.Add(minutes);
        await _context.SaveChangesAsync();
        return minutes;
    }

    [Fact]
    public async Task Revising_approved_minutes_succeeds()
    {
        var room = await SeedEndedRoomAsync();
        var approved = await SeedApprovedMinutesAsync(room, "BB-2026-0007");

        var result = await _service.ReviseAsync(room.Id, approved.Id, HostId);

        result.IsSuccess.Should().BeTrue(
            "a revision is the only way to correct an approved minutes; if this cannot commit, an " +
            "approved document can never be corrected at all. Error was: {0}", result.Error);
        result.Value!.Version.Should().Be(2);
        result.Value.Status.Should().Be(MeetingMinutesConstants.StatusDraft);
    }

    [Fact]
    public async Task The_revision_carries_the_same_minutes_number()
    {
        var room = await SeedEndedRoomAsync();
        var approved = await SeedApprovedMinutesAsync(room, "BB-2026-0007");

        var result = await _service.ReviseAsync(room.Id, approved.Id, HostId);

        // Renumbering would break every reference anybody had already written down. This is the
        // requirement that collides with the unfiltered unique index, so it is asserted rather
        // than assumed.
        result.Value!.MinutesNo.Should().Be("BB-2026-0007");
    }

    [Fact]
    public async Task The_signed_version_stays_on_record_and_surrenders_only_the_head_pointer()
    {
        var room = await SeedEndedRoomAsync();
        var approved = await SeedApprovedMinutesAsync(room, "BB-2026-0007");

        await _service.ReviseAsync(room.Id, approved.Id, HostId);
        _context.ChangeTracker.Clear();

        var versions = await _context.MeetingMinutes
            .Where(m => m.TranslationRoomId == room.Id)
            .OrderBy(m => m.Version)
            .ToListAsync();

        versions.Should().HaveCount(2);

        var v1 = versions[0];
        v1.Status.Should().Be(MeetingMinutesConstants.StatusApproved, "what was signed stays signed");
        v1.SecretarySignedAt.Should().NotBeNull();
        v1.ChairApprovedAt.Should().NotBeNull();
        v1.IsCurrent.Should().BeFalse();

        var v2 = versions[1];
        v2.IsCurrent.Should().BeTrue();
        v2.PreviousMinutesId.Should().Be(v1.Id);
        v2.SecretarySignedAt.Should().BeNull("a revision has not been signed yet");
        v2.ChairApprovedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_second_revision_stacks_on_the_first()
    {
        var room = await SeedEndedRoomAsync();
        var approved = await SeedApprovedMinutesAsync(room, "BB-2026-0007");

        var second = await _service.ReviseAsync(room.Id, approved.Id, HostId);
        second.IsSuccess.Should().BeTrue("error was: {0}", second.Error);

        // v2 must be approved before it can be revised in turn — that is the lifecycle, and it is
        // also what puts a third row on the same number through the index.
        var v2 = await _context.MeetingMinutes.FirstAsync(m => m.Id == second.Value!.Id);
        v2.Status = MeetingMinutesConstants.StatusApproved;
        v2.SecretarySignedAt = DateTime.UtcNow;
        v2.ChairApprovedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var third = await _service.ReviseAsync(room.Id, v2.Id, HostId);

        third.IsSuccess.Should().BeTrue("error was: {0}", third.Error);
        third.Value!.Version.Should().Be(3);
        third.Value.MinutesNo.Should().Be("BB-2026-0007");
    }

    [Fact]
    public async Task A_revision_does_not_consume_the_next_minutes_number()
    {
        // The number is allocated by counting what the workspace has already numbered this year.
        // A revision does not take a number — it keeps one — so it must not move that count, or
        // the next real meeting is numbered BB-2026-0009 and the register shows a gap where a
        // reader will look for a minutes that was never written.
        var room = await SeedEndedRoomAsync();
        var approved = await SeedApprovedMinutesAsync(room, "BB-2026-0007");

        var before = await _minutes.CountForWorkspaceYearAsync(_workspaceId, approved.CreatedAt.Year);

        var revision = await _service.ReviseAsync(room.Id, approved.Id, HostId);
        revision.IsSuccess.Should().BeTrue("error was: {0}", revision.Error);

        var after = await _minutes.CountForWorkspaceYearAsync(_workspaceId, approved.CreatedAt.Year);

        after.Should().Be(before, "the workspace still holds one numbered document");
    }

    [Fact]
    public async Task A_revision_carried_into_the_next_year_does_not_number_that_year()
    {
        // The count is per year, so a revision opened in January of the following year used to
        // register as that year's first document and push the real BB-2027-0001 to -0002.
        var room = await SeedEndedRoomAsync();
        var approved = await SeedApprovedMinutesAsync(room, "BB-2026-0007");

        var revision = await _service.ReviseAsync(room.Id, approved.Id, HostId);
        revision.IsSuccess.Should().BeTrue("error was: {0}", revision.Error);

        // ReviseAsync stamps the new row with UtcNow, which is the year the suite runs in — the
        // year the original document was NOT numbered in.
        var revisionYear = DateTime.UtcNow.Year;
        approved.CreatedAt = new DateTime(revisionYear - 1, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        await _context.SaveChangesAsync();

        var thisYear = await _minutes.CountForWorkspaceYearAsync(_workspaceId, revisionYear);

        thisYear.Should().Be(0, "no document has been numbered this year — only a revision of last year's");
    }

    [Fact]
    public async Task Two_rooms_in_one_workspace_still_cannot_share_a_number()
    {
        // The race guard the number allocation leans on: two secretaries counting at the same
        // instant are handed the same string, and the database rejects the loser. Widening the
        // index to admit revisions must not widen it enough to admit this.
        var roomA = await SeedEndedRoomAsync("Room A");
        var roomB = await SeedEndedRoomAsync("Room B");

        await SeedApprovedMinutesAsync(roomA, "BB-2026-0007");

        var act = async () => await SeedApprovedMinutesAsync(roomB, "BB-2026-0007");

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}

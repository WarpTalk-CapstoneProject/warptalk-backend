using System;
using System.Text.Json;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// The workspace-wide minutes list, and the boundary it must not widen.
///
/// WHY THIS RUNS AGAINST A REAL DATABASE
///     The whole endpoint IS an EF query. Its authority is a subquery — "minutes whose room is
///     readable by this caller" — expressed as an expression tree that Postgres executes. A
///     mock-backed test would assert the shape of code that never runs as written, and the one
///     failure that matters here (a document listed for somebody the per-room route would refuse)
///     lives entirely on that boundary.
///
/// WHAT IS ACTUALLY BEING GUARDED
///     A per-room read is a narrow door somebody has to already be standing at. A workspace-wide
///     list is a wide one, and turning "documents about meetings I was in" into "every document in
///     the tenant" is a one-line mistake with no visible symptom — the page simply looks fuller.
/// </summary>
public class WorkspaceMinutesLibraryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private TranslationRoomDbContext _dbContext = null!;
    private MeetingMinutesService _service = null!;

    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AttendeeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StrangerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        var options = new DbContextOptionsBuilder<TranslationRoomDbContext>()
            .UseNpgsql(_dbContainer.GetConnectionString())
            .Options;
        _dbContext = new TranslationRoomDbContext(options);

        await _dbContext.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        await _dbContext.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION public.uuidv7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await _dbContext.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION public.uuid_generate_v7() RETURNS uuid AS $$ BEGIN RETURN gen_random_uuid(); END; $$ LANGUAGE plpgsql;");
        await _dbContext.Database.EnsureCreatedAsync();

        var unitOfWork = new UnitOfWork(
            _dbContext,
            new TranslationRoomRepository(_dbContext),
            new TranslationRoomParticipantRepository(_dbContext),
            new TranslationRoomAudioRouteRepository(_dbContext),
            new LanguageRepository(_dbContext),
            new TranslationRoomArtifactRepository(_dbContext),
            new TranslationRoomSessionRepository(_dbContext),
            new TranslationRoomInvitationRepository(_dbContext),
            new TranslationRoomFeedbackRepository(_dbContext),
            new TranslationRoomSeriesRepository(_dbContext),
            new MeetingMinutesRepository(_dbContext),
            new MeetingActionItemRepository(_dbContext));

        _service = new MeetingMinutesService(
            unitOfWork,
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<IMeetingMinutesDocumentWriter>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<MeetingMinutesService>>().Object);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    /// <summary>
    /// A meeting with a current biên bản. <paramref name="attendeeIds"/> get participant rows;
    /// nobody else has any relationship to the room at all.
    /// </summary>
    private async Task<MeetingMinutes> SeedMeetingWithMinutesAsync(
        string title,
        string content,
        DateTime endedAt,
        string status = MeetingMinutesConstants.StatusApproved,
        Guid? hostId = null,
        params Guid[] attendeeIds)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = WorkspaceId,
            HostId = hostId ?? HostId,
            Title = title,
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
            Status = "ENDED",
            TranslationRoomType = "INSTANT",
            MaxParticipants = 100,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            IsActive = true,
            EndedAt = endedAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Set<TranslationRoom>().Add(room);

        foreach (var attendeeId in attendeeIds)
        {
            _dbContext.Set<TranslationRoomParticipant>().Add(new TranslationRoomParticipant
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = room.Id,
                UserId = attendeeId,
                DisplayName = $"User {attendeeId.ToString()[..4]}",
                Role = "PARTICIPANT",
                ListenLanguage = "en",
                SpeakLanguage = "vi",
                Status = "LEFT",
                ConnectionType = "WEBRTC",
                IsTranslationAudioEnabled = true,
                IsUsingVoiceClone = false,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var minutes = new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room.Id,
            WorkspaceId = WorkspaceId,
            MinutesNo = $"BB-{now.Year}-{Random.Shared.Next(1000, 9999)}",
            Status = status,
            // Content = WithMeetingTitle(...) below; see the helper for why the title is injected
            // rather than left to each caller.
            Version = 1,
            IsCurrent = true,
            EditCountVsDraft = 0,
            Content = WithMeetingTitle(content, title),
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Set<MeetingMinutes>().Add(minutes);

        await _dbContext.SaveChangesAsync();
        // The list must answer from the database, not from rows this test just inserted.
        _dbContext.ChangeTracker.Clear();
        return minutes;
    }


    /// <summary>
    /// The seeded content with a <c>meetingTitle</c>, as MeetingMinutesDrafter always writes one.
    ///
    /// Without this the seeded rows are unlike anything production holds: the drafter copies the
    /// room's title into the document at draft time, so every real row has this key. Tests that
    /// omitted it were asserting against a document with no title, which is exactly the shape the
    /// library's title search cannot find — a false failure, or worse a false pass.
    ///
    /// A title already in the passed content wins, so a test can seed a document whose title
    /// deliberately differs from its room.
    /// </summary>
    private static string WithMeetingTitle(string contentJson, string title)
    {
        using var doc = JsonDocument.Parse(contentJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return contentJson;
        if (doc.RootElement.TryGetProperty("meetingTitle", out _)) return contentJson;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("meetingTitle", title);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private Task<WorkspaceMinutesResponse> ListAsync(Guid userId, GetWorkspaceMinutesRequest? request = null)
        => ListAsync(userId, null, request);

    private async Task<WorkspaceMinutesResponse> ListAsync(
        Guid userId, string? email, GetWorkspaceMinutesRequest? request = null)
    {
        var result = await _service.ListForWorkspaceAsync(
            WorkspaceId, request ?? new GetWorkspaceMinutesRequest(), userId, email, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    /// <summary>
    /// The list is scoped by the room, not by the workspace.
    ///
    /// A member of the tenant who was not at the meeting and was never invited to it has no claim
    /// on its minutes — the same answer <c>GetCurrentAsync</c> gives one room at a time.
    /// </summary>
    [Fact]
    public async Task List_ShowsOnlyMinutesOfMeetingsTheCallerMayRead()
    {
        await SeedMeetingWithMinutesAsync(
            "Sprint review", "{\"agenda\":\"sprint\"}", DateTime.UtcNow.AddDays(-1),
            attendeeIds: AttendeeId);

        var host = await ListAsync(HostId);
        var attendee = await ListAsync(AttendeeId);
        var stranger = await ListAsync(StrangerId);

        host.Items.Should().ContainSingle().Which.RoomTitle.Should().Be("Sprint review");
        attendee.Items.Should().ContainSingle("someone who was in the meeting can read its record");
        stranger.Items.Should().BeEmpty("a workspace member who was not there has no claim on it");
        stranger.Total.Should().Be(0, "Total must count the caller's rows, not the workspace's");
    }

    /// <summary>
    /// An unaccepted invitation still grants read — <c>RoomReadAccess</c> says so, and this list
    /// must not answer that question its own way.
    /// </summary>
    [Fact]
    public async Task List_IncludesMeetingsReachedByAStandingInvitation()
    {
        var minutes = await SeedMeetingWithMinutesAsync(
            "Kickoff", "{}", DateTime.UtcNow.AddDays(-2));

        var room = await _dbContext.Set<MeetingMinutes>()
            .Where(m => m.Id == minutes.Id)
            .Select(m => m.TranslationRoomId)
            .SingleAsync();

        _dbContext.Set<TranslationRoomInvitation>().Add(new TranslationRoomInvitation
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room,
            // Mixed case on purpose: WT-496's fold has to hold here too.
            Email = "Invited@Example.com",
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var invited = await ListAsync(StrangerId, "invited@example.com");

        invited.Items.Should().ContainSingle();
    }

    /// <summary>
    /// Newest MEETING first, not newest document. The two orders differ whenever minutes are
    /// drawn up out of order, which is the ordinary case — a backlog gets written up in one
    /// sitting, days after the meetings it describes.
    /// </summary>
    [Fact]
    public async Task List_OrdersByWhenTheMeetingEnded()
    {
        await SeedMeetingWithMinutesAsync("Older meeting", "{}", DateTime.UtcNow.AddDays(-9));
        await SeedMeetingWithMinutesAsync("Newer meeting", "{}", DateTime.UtcNow.AddDays(-1));

        var page = await ListAsync(HostId);

        page.Items.Select(item => item.RoomTitle)
            .Should().ContainInOrder("Newer meeting", "Older meeting");
    }

    /// <summary>
    /// Search matches the meeting, case-insensitively.
    ///
    /// The document's PROSE is not searched: one key is extracted by name, the sections are not
    /// scanned, and the room history does not search artifact bodies either. The web folds and
    /// searches the bodies of the page it has loaded, so every kind of record in the library
    /// behaves the same way.
    /// </summary>
    [Fact]
    public async Task Search_MatchesTheMeetingItBelongsTo()
    {
        await SeedMeetingWithMinutesAsync(
            "Q4 budget review", "{\"decisions\":[\"Approve the budget\"]}", DateTime.UtcNow.AddDays(-3));
        await SeedMeetingWithMinutesAsync(
            "Design review", "{\"decisions\":[\"Ship the new icon set\"]}", DateTime.UtcNow.AddDays(-2));

        var found = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Search: "q4 BUDGET"));

        found.Items.Should().ContainSingle().Which.RoomTitle.Should().Be("Q4 budget review");
        found.Total.Should().Be(1, "Total reflects the filter, or the pager lies about what is behind it");
    }

    /// <summary>
    /// Renaming the room does not retitle the documents it already produced.
    ///
    /// This is the case the library used to get wrong. It listed and searched
    /// <c>TranslationRoom.Title</c>, read live through the navigation property, while both .docx
    /// writers print <c>content.meetingTitle</c> — a snapshot taken once when the draft was built.
    /// Rename a room after its minutes were signed and the card said one thing while the file that
    /// downloaded said another, with nothing to tell a reader which the signatories had seen.
    ///
    /// The snapshot is the right answer: a biên bản records a moment, and letting a rename change
    /// the title of an APPROVED document edits a signed record without a revision — the thing
    /// ReviseAsync and the immutability rule exist to prevent.
    /// </summary>
    [Fact]
    public async Task Renaming_the_room_does_not_change_what_the_minutes_are_called()
    {
        var minutes = await SeedMeetingWithMinutesAsync(
            "Q4 budget review", "{}", DateTime.UtcNow.AddDays(-3));

        await _dbContext.Set<TranslationRoom>()
            .Where(r => r.Id == minutes.TranslationRoomId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Title, "Weekly sync"));
        _dbContext.ChangeTracker.Clear();

        var byOriginalName = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Search: "q4 BUDGET"));
        byOriginalName.Items.Should().ContainSingle(
            "the document is still called what it was called when it was drawn up");
        byOriginalName.Items[0].Minutes.MeetingTitle.Should().Be("Q4 budget review");

        var byNewRoomName = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Search: "weekly sync"));
        byNewRoomName.Items.Should().BeEmpty(
            "the library searches the documents it lists, not what their rooms happen to be called now");
    }

    /// <summary>
    /// The title lives inside a jsonb column, so this is the test that the clause translates to SQL
    /// at all — lower() cannot be applied to jsonb, and the extraction is a mapped Postgres
    /// function rather than anything Npgsql surfaces on EF.Functions.
    /// </summary>
    [Fact]
    public async Task Search_MatchesATitleStoredInsideTheDocument()
    {
        await SeedMeetingWithMinutesAsync(
            "Design review", "{\"decisions\":[\"Ship the new icon set\"]}", DateTime.UtcNow.AddDays(-2));

        var found = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Search: "DESIGN"));

        found.Items.Should().ContainSingle();
        found.Items[0].Minutes.MeetingTitle.Should().Be("Design review");
    }

    /// <summary>
    /// A document whose content records no title is not found by title — and, more importantly,
    /// does not match every search. The extraction yields SQL NULL, and NULL LIKE '%x%' is NULL.
    /// </summary>
    [Fact]
    public async Task A_document_with_no_recorded_title_does_not_match_everything()
    {
        // Seeded with an explicit null title, so WithMeetingTitle leaves it alone.
        await SeedMeetingWithMinutesAsync(
            "Untitled meeting", "{\"meetingTitle\":null}", DateTime.UtcNow.AddDays(-1));

        var found = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Search: "anything at all"));

        found.Items.Should().BeEmpty();
        found.Total.Should().Be(0);
    }

    /// <summary>The document number is how a reader who filed it on paper finds it again.</summary>
    [Fact]
    public async Task Search_MatchesTheMinutesNumber()
    {
        var minutes = await SeedMeetingWithMinutesAsync(
            "Any meeting", "{}", DateTime.UtcNow.AddDays(-1));

        var found = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Search: minutes.MinutesNo));

        found.Items.Should().ContainSingle();
    }

    /// <summary>
    /// A superseded version is one meeting's paper trail, not a second library row.
    ///
    /// The superseded row carries the SAME MinutesNo as the current one, which is what a real
    /// revision produces — ReviseAsync deliberately keeps the number ("a revision of BB-2026-0007
    /// is still BB-2026-0007"). That shape used to be unseedable, because
    /// meeting_minutes_workspace_no_idx was UNIQUE on (workspace_id, minutes_no) and rejected the
    /// second row; this test varied the number to work around it. The index is now keyed on
    /// (workspace_id, minutes_no, version), so the real shape is the one asserted here — which
    /// matters, because "only the current version" is exactly the claim a shared number tests.
    /// </summary>
    [Fact]
    public async Task List_ShowsOnlyTheCurrentVersion()
    {
        // The helper seeds version 1 holding the head pointer. A revision moves that pointer on, so
        // the row it seeded becomes the SUPERSEDED one and the head becomes version 2 — the order
        // ReviseAsync actually writes, rather than a superseded v2 sitting behind a current v1.
        var superseded = await SeedMeetingWithMinutesAsync(
            "Board meeting", "{}", DateTime.UtcNow.AddDays(-4));

        // Surrendered before the new head is inserted, and as its own statement: both rows would
        // otherwise claim `is_current`, and meeting_minutes_one_current_per_room_idx will not have
        // two — EF is free to order an INSERT ahead of an UPDATE within one batch, and here it does.
        //
        // Written through the database rather than by assigning to `superseded`, because the seed
        // helper clears the change tracker before returning: the entity in hand is detached, so a
        // property set on it produces no UPDATE at all.
        await _dbContext.Set<MeetingMinutes>()
            .Where(m => m.Id == superseded.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsCurrent, false));

        _dbContext.Set<MeetingMinutes>().Add(new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = superseded.TranslationRoomId,
            WorkspaceId = WorkspaceId,
            // Same number, as a real revision keeps it.
            MinutesNo = superseded.MinutesNo,
            Status = MeetingMinutesConstants.StatusApproved,
            // 2, not 0: Version is mapped HasDefaultValue(1), so EF omits the column for the CLR
            // default and Postgres writes 1 — colliding with the seeded row on
            // meeting_minutes_room_version_idx rather than seeding a later version.
            Version = 2,
            IsCurrent = true,
            PreviousMinutesId = superseded.Id,
            EditCountVsDraft = 0,
            Content = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var page = await ListAsync(HostId);

        page.Items.Should().ContainSingle();
    }

    /// <summary>
    /// A caller-supplied page size cannot ask for the whole archive. Every row carries its whole
    /// Content document, so the ceiling is a memory bound, not a nicety.
    /// </summary>
    [Fact]
    public async Task PageSize_IsClampedToTheCeiling()
    {
        await SeedMeetingWithMinutesAsync("Any meeting", "{}", DateTime.UtcNow.AddDays(-1));

        var page = await ListAsync(HostId, new GetWorkspaceMinutesRequest(PageSize: 100_000));

        page.PageSize.Should().Be(200);
    }

    /// <summary>Status is how a reader finds what has not been signed yet.</summary>
    [Fact]
    public async Task Status_NarrowsToOneStageOfTheLifecycle()
    {
        await SeedMeetingWithMinutesAsync(
            "Signed one", "{}", DateTime.UtcNow.AddDays(-2), MeetingMinutesConstants.StatusApproved);
        await SeedMeetingWithMinutesAsync(
            "Unsigned one", "{}", DateTime.UtcNow.AddDays(-1), MeetingMinutesConstants.StatusDraft);

        var drafts = await ListAsync(HostId, new GetWorkspaceMinutesRequest(Status: "draft"));

        drafts.Items.Should().ContainSingle().Which.RoomTitle.Should().Be("Unsigned one");
    }

    /// <summary>
    /// A workspace has to be named. Without this the query would read every minutes row the
    /// caller can reach across every tenant they belong to — a cross-tenant list nobody asked for.
    /// </summary>
    [Fact]
    public async Task List_RefusesWithoutAWorkspace()
    {
        var result = await _service.ListForWorkspaceAsync(
            Guid.Empty, new GetWorkspaceMinutesRequest(), HostId, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }
}

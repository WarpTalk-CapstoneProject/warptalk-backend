using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// A workspace ADMIN who did not create a room, was never in it, and was never personally invited by
/// email saw NOTHING: "No active meetings found." and a dashboard tile reading 0, for a workspace
/// that had rooms in it. The room's own detail page opened for that same account by direct URL, with
/// a working Join button — so the list was stricter than the thing it was a list of, and because the
/// Join control lives only on the detail page, an empty list left her no route into any meeting.
///
/// The list knew three ways in — host, prior participant, invited-by-email — and workspace
/// Owner/Admin was not one of them, because it is not a fact the translation-room database holds.
/// WT-313 had already ratified host OR participant OR workspace Owner/Admin as the rule for who may
/// act on a room; it audited TranslationRoomParticipantService and never reached the rooms list.
///
/// A plain workspace MEMBER is deliberately still outside the widening — WT-313 keeps that as a
/// negative case — which the last test here pins.
///
/// Real Postgres on purpose, matching <see cref="RoomOccupancyCountTests"/>: the list path runs
/// CountAsync/ToListAsync over the repository's IQueryable, which a mock-backed in-memory sequence
/// cannot execute, so a unit test here would have to assert against a different query than the one
/// that ships.
/// </summary>
public class WorkspaceAdminRoomVisibilityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private TranslationRoomDbContext _dbContext = null!;
    private WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service = null!;
    private readonly Mock<IWorkspaceMemberDirectory> _workspaceMembers = new();

    private static readonly Guid WorkspaceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid HostId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>An Admin of the workspace, and nothing else: not the host, never a participant,
    /// never invited by email. This is the account the defect was reported against — the mentor who
    /// could not find, and therefore could not join, a room in the workspace she administers.</summary>
    private static readonly Guid WorkspaceAdminId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>A plain member of the workspace. Deliberately NOT widened: WT-313 keeps a plain
    /// member as a negative case, so this account must keep seeing only its own rooms.</summary>
    private static readonly Guid PlainMemberId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    /// <summary>Not a member of the workspace at all. Must keep seeing nothing.</summary>
    private static readonly Guid OutsiderId = Guid.Parse("66666666-6666-6666-6666-666666666666");

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
            new MeetingMinutesRepository(_dbContext));

        var languagePolicy = new Mock<ILanguagePolicy>();
        languagePolicy.Setup(p => p.IsSupportedAsync(It.IsAny<string>())).ReturnsAsync(true);

        _workspaceMembers
            .Setup(d => d.IsOwnerOrAdminAsync(WorkspaceId, WorkspaceAdminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            unitOfWork,
            languagePolicy.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            new Mock<IUserSettingsDirectory>().Object,
            new Mock<IWorkspaceMeetingPolicy>().Object,
            _workspaceMembers.Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<
                WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    private async Task<TranslationRoom> SeedRoomAsync(
        Guid workspaceId,
        string status,
        Guid? hostId = null,
        DateTime? scheduledAt = null,
        DateTime? endedAt = null)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            HostId = hostId ?? HostId,
            Title = $"Room {status}",
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
            Status = status,
            TranslationRoomType = "INSTANT",
            MaxParticipants = 100,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            ScheduledAt = scheduledAt,
            EndedAt = endedAt ?? (status == "ENDED" ? now : null)
        };

        _dbContext.Set<TranslationRoom>().Add(room);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return room;
    }

    private async Task SeedParticipantAsync(Guid roomId, Guid userId)
    {
        var now = DateTime.UtcNow;
        _dbContext.Set<TranslationRoomParticipant>().Add(new TranslationRoomParticipant
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            UserId = userId,
            DisplayName = "Participant",
            Role = "PARTICIPANT",
            ListenLanguage = "en",
            SpeakLanguage = "vi",
            Status = "CONNECTED",
            ConnectionType = "webrtc",
            JoinedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task SeedInvitationAsync(Guid roomId, string email)
    {
        var now = DateTime.UtcNow;
        _dbContext.Set<TranslationRoomInvitation>().Add(new TranslationRoomInvitation
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            Email = email,
            Status = "PENDING",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task SeedArtifactAsync(Guid roomId, string content)
    {
        _dbContext.Set<TranslationRoomArtifact>().Add(new TranslationRoomArtifact
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = roomId,
            ArtifactType = "SUMMARY_EXPORT",
            FileFormat = "md",
            Content = content,
            Status = "COMPLETED",
            CreatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private Task<WarpTalk.Shared.Result<TranslationRoomListResponse>> ListAsync(Guid userId) =>
        _service.GetTranslationRoomsAsync(
            new GetTranslationRoomsRequest(WorkspaceId: WorkspaceId, PageSize: 100),
            userId,
            $"{userId}@example.test");

    private Task<WarpTalk.Shared.Result<TranslationRoomHistoryResponse>> HistoryAsync(Guid userId) =>
        _service.GetTranslationRoomHistoryAsync(
            new GetTranslationRoomsRequest(WorkspaceId: WorkspaceId, PageSize: 100),
            userId,
            $"{userId}@example.test");

    [Fact]
    public async Task ActiveList_ShowsWorkspaceRooms_ToAnAdminWhoIsNeitherHostNorParticipantNorInvitee()
    {
        var room = await SeedRoomAsync(WorkspaceId, "WAITING");

        var result = await ListAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Id == room.Id);
        result.Value.Total.Should().Be(1);
    }

    /// <summary>
    /// The second caller of the same query builder, and the reason history was empty for the same
    /// account for the same reason. Fixing only the active list would have left the History tab
    /// still claiming the workspace had never held a meeting.
    /// </summary>
    [Fact]
    public async Task History_ShowsWorkspaceRooms_ToAnAdminWhoIsNeitherHostNorParticipantNorInvitee()
    {
        var room = await SeedRoomAsync(WorkspaceId, "ENDED");

        var result = await HistoryAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Room.Id == room.Id);
    }

    /// <summary>
    /// The opposite error the widening must not make. Someone outside the workspace still gets the
    /// host/participant/invitation answer, which for them is nothing.
    /// </summary>
    [Fact]
    public async Task ActiveList_StaysEmpty_ForSomebodyWhoIsNotInTheWorkspace()
    {
        await SeedRoomAsync(WorkspaceId, "WAITING");

        var result = await ListAsync(OutsiderId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().BeEmpty();
        result.Value.Total.Should().Be(0);
    }

    /// <summary>
    /// The role widens ONE workspace, not the instance. An Admin of this workspace must not inherit
    /// sight of another workspace's rooms.
    /// </summary>
    [Fact]
    public async Task ActiveList_DoesNotLeakRoomsFromAnotherWorkspace()
    {
        var otherWorkspaceId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        await SeedRoomAsync(otherWorkspaceId, "WAITING");
        var ownRoom = await SeedRoomAsync(WorkspaceId, "WAITING");

        var result = await ListAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Id == ownRoom.Id);
    }

    /// <summary>
    /// The scope boundary, stated as a test so it cannot be widened by accident. WT-313 settled that
    /// a plain workspace Member is NOT host-adjacent, and this change deliberately does not reopen
    /// that. A member who neither hosts, has joined, nor was invited to a room still does not see it.
    /// </summary>
    [Fact]
    public async Task ActiveList_StaysEmpty_ForAPlainWorkspaceMember()
    {
        await SeedRoomAsync(WorkspaceId, "WAITING");

        var result = await ListAsync(PlainMemberId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // WT-333 — My Meetings (UC 25).
    //
    // The widening the tests above pin is correct for the workspace archive and wrong for a
    // personal timeline: it runs BEFORE every filter, so an Owner/Admin asking "what are MY
    // meetings" had no request that could mean it. These tests pin the narrowed read, and they
    // live beside the widening on purpose — the two are the same decision seen from both sides,
    // and separating them is how one gets changed without the other.
    // ---------------------------------------------------------------------------------------

    private Task<WarpTalk.Shared.Result<TranslationRoomHistoryResponse>> MyMeetingsAsync(Guid userId) =>
        _service.GetMyMeetingsAsync(
            new GetTranslationRoomsRequest(WorkspaceId: WorkspaceId, PageSize: 100),
            userId,
            $"{userId}@example.test");

    /// <summary>
    /// The bug this feature exists for. The same Admin the widening was built for must NOT get the
    /// whole workspace back here — only what she is actually part of.
    /// </summary>
    [Fact]
    public async Task MyMeetings_ExcludesWorkspaceRooms_TheAdminIsNoPartOf()
    {
        await SeedRoomAsync(WorkspaceId, "ENDED");
        var ownRoom = await SeedRoomAsync(WorkspaceId, "ENDED", hostId: WorkspaceAdminId);

        var result = await MyMeetingsAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Room.Id == ownRoom.Id);
        result.Value.Total.Should().Be(1);
    }

    /// <summary>
    /// The half of the timeline the archive cannot show. A meeting somebody was invited to but has
    /// not attended — and which has not happened — is exactly what a personal timeline is for, and
    /// it is filtered out of history twice over: by status and by never having ended.
    /// </summary>
    [Fact]
    public async Task MyMeetings_IncludesAnUpcomingRoom_ForAnInviteeWhoHasNotJoined()
    {
        var upcoming = await SeedRoomAsync(
            WorkspaceId, "SCHEDULED", scheduledAt: DateTime.UtcNow.AddDays(1));
        await SeedInvitationAsync(upcoming.Id, $"{OutsiderId}@example.test");

        var result = await MyMeetingsAsync(OutsiderId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Room.Id == upcoming.Id);
    }

    /// <summary>
    /// Invitation visibility is resolved by RoomReadAccess for the personal timeline too. Keeping
    /// the past invitation case here prevents My Meetings from drifting into a second read policy.
    /// </summary>
    [Fact]
    public async Task MyMeetings_IncludesAPastRoom_ForAnInviteeByEmail()
    {
        var past = await SeedRoomAsync(WorkspaceId, "ENDED");
        await SeedInvitationAsync(past.Id, $"{OutsiderId}@example.test");

        var result = await MyMeetingsAsync(OutsiderId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Room.Id == past.Id);
        result.Value.Total.Should().Be(1);
    }

    /// <summary>
    /// Live keeps the invitation path: the caller has not joined yet, but the row is precisely the
    /// shortcut that lets them do so.
    /// </summary>
    [Fact]
    public async Task MyMeetings_IncludesALiveRoom_ForAnInviteeWhoHasNotJoined()
    {
        var live = await SeedRoomAsync(WorkspaceId, "WAITING");
        await SeedInvitationAsync(live.Id, $"{OutsiderId}@example.test");

        var result = await MyMeetingsAsync(OutsiderId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Room.Id == live.Id);
    }

    /// <summary>
    /// Backend defaults My Meetings to every declared room status. That keeps the API's default
    /// window tied to the domain enum, while still excluding database values the enum does not know.
    /// </summary>
    [Fact]
    public async Task MyMeetings_DefaultStatusWindow_UsesEveryDeclaredRoomStatus()
    {
        var expectedStatuses = Enum.GetNames<RoomStatus>();

        foreach (var status in expectedStatuses)
        {
            await SeedRoomAsync(WorkspaceId, status, hostId: WorkspaceAdminId);
        }

        await SeedRoomAsync(WorkspaceId, "ARCHIVED", hostId: WorkspaceAdminId);

        var result = await MyMeetingsAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Select(r => r.Room.Status.ToString())
            .Should()
            .BeEquivalentTo(expectedStatuses);
    }

    /// <summary>
    /// Widening WHICH ROOMS a caller sees must not widen WHAT IS IN THEM. A room whose ArtifactAccess
    /// is HOST_ONLY — which "{}" settings resolve to — keeps its AI summary from a participant here,
    /// exactly as the download endpoint does. This is the WT-304 drift, pinned on the new route
    /// before it can happen a fourth time.
    /// </summary>
    [Fact]
    public async Task MyMeetings_WithholdsArtifactContent_FromAParticipantOfAHostOnlyRoom()
    {
        var room = await SeedRoomAsync(WorkspaceId, "ENDED");
        await SeedParticipantAsync(room.Id, PlainMemberId);
        await SeedArtifactAsync(room.Id, "secret summary");

        var result = await MyMeetingsAsync(PlainMemberId);

        result.IsSuccess.Should().BeTrue(result.Error);
        var artifacts = result.Value!.Rooms.Single(r => r.Room.Id == room.Id).Artifacts;
        artifacts.Should().ContainSingle();
        artifacts[0].Content.Should().BeNull();
    }

    /// <summary>
    /// The host of that same room does get it, so the test above is pinning the policy rather than a
    /// projection that simply never fills the field in.
    /// </summary>
    [Fact]
    public async Task MyMeetings_ReturnsArtifactContent_ToTheHost()
    {
        var room = await SeedRoomAsync(WorkspaceId, "ENDED", hostId: WorkspaceAdminId);
        await SeedArtifactAsync(room.Id, "secret summary");

        var result = await MyMeetingsAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        var artifacts = result.Value!.Rooms.Single(r => r.Room.Id == room.Id).Artifacts;
        artifacts.Should().ContainSingle();
        artifacts[0].Content.Should().Be("secret summary");
    }

    /// <summary>
    /// The ordering trap. A future room has neither EndedAt nor StartedAt, so the archive's ordering
    /// falls through to CreatedAt and would place a meeting by the day somebody booked it. Seeded so
    /// that CreatedAt ordering gives the WRONG answer: both rows are created now, and the upcoming
    /// room is created FIRST, so only ScheduledAt can put it on top.
    /// </summary>
    [Fact]
    public async Task MyMeetings_OrdersByTheDayAMeetingHappens_NotTheDayItWasBooked()
    {
        var upcoming = await SeedRoomAsync(
            WorkspaceId, "SCHEDULED", hostId: WorkspaceAdminId, scheduledAt: DateTime.UtcNow.AddDays(1));
        var past = await SeedRoomAsync(
            WorkspaceId, "ENDED", hostId: WorkspaceAdminId, endedAt: DateTime.UtcNow.AddDays(-1));

        var result = await MyMeetingsAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Select(r => r.Room.Id).Should().ContainInOrder(upcoming.Id, past.Id);
    }

    /// <summary>
    /// The tenant boundary the cross-workspace variant of this feature was dropped to preserve.
    /// Narrowing to "mine" must not become "mine everywhere".
    /// </summary>
    [Fact]
    public async Task MyMeetings_DoesNotLeakTheCallersOwnRoomsFromAnotherWorkspace()
    {
        var otherWorkspaceId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        await SeedRoomAsync(otherWorkspaceId, "ENDED", hostId: WorkspaceAdminId);
        var ownRoom = await SeedRoomAsync(WorkspaceId, "ENDED", hostId: WorkspaceAdminId);

        var result = await MyMeetingsAsync(WorkspaceAdminId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Rooms.Should().ContainSingle(r => r.Room.Id == ownRoom.Id);
    }

    /// <summary>
    /// WorkspaceId stays required. Left unpinned, "personal timeline" is one forgotten query
    /// parameter away from being a cross-tenant read.
    /// </summary>
    [Fact]
    public async Task MyMeetings_IsRejected_WithoutAWorkspaceId()
    {
        await SeedRoomAsync(WorkspaceId, "ENDED", hostId: WorkspaceAdminId);

        var result = await _service.GetMyMeetingsAsync(
            new GetTranslationRoomsRequest(PageSize: 100),
            WorkspaceAdminId,
            $"{WorkspaceAdminId}@example.test");

        result.IsSuccess.Should().BeFalse();
    }
}

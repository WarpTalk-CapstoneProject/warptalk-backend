using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
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
            new TranslationRoomSeriesRepository(_dbContext));

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

    private async Task<TranslationRoom> SeedRoomAsync(Guid workspaceId, string status)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            HostId = HostId,
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
            EndedAt = status == "ENDED" ? now : null
        };

        _dbContext.Set<TranslationRoom>().Add(room);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return room;
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
}

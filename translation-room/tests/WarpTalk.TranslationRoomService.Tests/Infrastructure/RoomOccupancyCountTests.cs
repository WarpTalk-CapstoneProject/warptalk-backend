using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// WT-280. Occupancy ("3/100" on the rooms list) was reported as
/// <c>room.TranslationRoomParticipants.Count</c>: every row regardless of status, and — because
/// the list query never Includes that navigation — silently 0 whenever it was not loaded. That is
/// exactly the observed production symptom: a room with a CONNECTED host rendering as 0/100.
///
/// These run against a real Postgres on purpose. The defect lives entirely on the EF boundary: an
/// unloaded navigation collection is empty in memory and raises nothing, so every mock-backed test
/// that hands the service a hand-built entity graph would report the "right" number for the wrong
/// reason and stay green while production reported 0.
/// </summary>
public class RoomOccupancyCountTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private TranslationRoomDbContext _dbContext = null!;
    private WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService _service = null!;

    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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

        var meetingPolicy = new Mock<IWorkspaceMeetingPolicy>();
        meetingPolicy.Setup(p => p.ValidateMeetingCreationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        // ...and the tenant itself is live unless a test suspends it.
        meetingPolicy.Setup(p => p.EnsureWorkspaceCanHostMeetingsAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _service = new WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService(
            unitOfWork,
            languagePolicy.Object,
            new Mock<IAudioRouteEventProcessor>().Object,
            new Mock<ITranslationRoomAudioRouteService>().Object,
            new Mock<IUserSettingsDirectory>().Object,
            // meetingPolicy (not a bare mock) — development's suspension gate needs
            // EnsureWorkspaceCanHostMeetingsAsync stubbed, or every room here fails closed.
            meetingPolicy.Object,
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<WarpTalk.Shared.Interfaces.IEmailService>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<
                WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>().Object);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _dbContainer.DisposeAsync();
    }

    private async Task<TranslationRoom> SeedRoomAsync(params string[] participantStatuses)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = WorkspaceId,
            HostId = HostId,
            Title = "Occupancy room",
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
            Status = "WAITING",
            TranslationRoomType = "INSTANT",
            MaxParticipants = 100,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Set<TranslationRoom>().Add(room);

        foreach (var status in participantStatuses)
        {
            _dbContext.Set<TranslationRoomParticipant>().Add(new TranslationRoomParticipant
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = room.Id,
                UserId = HostId,
                DisplayName = $"P-{status}",
                Role = "PARTICIPANT",
                ListenLanguage = "en",
                SpeakLanguage = "vi",
                Status = status,
                ConnectionType = "WEBRTC",
                IsTranslationAudioEnabled = true,
                IsUsingVoiceClone = false,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _dbContext.SaveChangesAsync();

        // Nothing in the service's list path Includes the participants navigation, and a
        // ChangeTracker still holding the rows we just inserted would mask that. Detach so the
        // query answers from the database, exactly as a fresh scoped DbContext does in production.
        _dbContext.ChangeTracker.Clear();
        return room;
    }

    private async Task<TranslationRoomListItemDto> ListRoomAsync(Guid roomId)
    {
        var result = await _service.GetTranslationRoomsAsync(
            new GetTranslationRoomsRequest(WorkspaceId: WorkspaceId, PageSize: 100),
            HostId);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!.Rooms.Single(r => r.Id == roomId);
    }

    /// <summary>
    /// A seat is held only by a CONNECTED participant (the ratified rule pinned in
    /// <see cref="TranslationRoomParticipantStatuses.SeatHolding"/>). Rows that left, were kicked or
    /// rejected, or are still sitting in the lobby must not be billed as occupancy.
    /// </summary>
    [Fact]
    public async Task Occupancy_CountsOnlyConnectedParticipants()
    {
        var room = await SeedRoomAsync(
            TranslationRoomParticipantStatuses.Connected,
            TranslationRoomParticipantStatuses.Connected,
            TranslationRoomParticipantStatuses.Left,
            TranslationRoomParticipantStatuses.Kicked,
            TranslationRoomParticipantStatuses.Rejected,
            TranslationRoomParticipantStatuses.Waiting,
            TranslationRoomParticipantStatuses.Disconnected,
            TranslationRoomParticipantStatuses.Invited);

        var listed = await ListRoomAsync(room.Id);

        listed.ParticipantCount.Should().Be(2);
    }

    /// <summary>
    /// The production symptom: one CONNECTED host, rendered as 0/100. The list query does not
    /// eager-load TranslationRoomParticipants, so counting the navigation collection returned 0
    /// without any error to notice.
    /// </summary>
    [Fact]
    public async Task Occupancy_DoesNotCollapseToZero_WhenTheParticipantsCollectionIsNotEagerlyLoaded()
    {
        var room = await SeedRoomAsync(TranslationRoomParticipantStatuses.Connected);

        var listed = await ListRoomAsync(room.Id);

        listed.ParticipantCount.Should().Be(1);
    }

    /// <summary>An empty room is genuinely 0 — the fix must not turn every room into a non-zero.</summary>
    [Fact]
    public async Task Occupancy_IsZero_WhenNobodyHoldsASeat()
    {
        var room = await SeedRoomAsync(
            TranslationRoomParticipantStatuses.Left,
            TranslationRoomParticipantStatuses.Waiting);

        var listed = await ListRoomAsync(room.Id);

        listed.ParticipantCount.Should().Be(0);
    }

    /// <summary>
    /// WT-280: room DETAIL carried no ParticipantCount at all, so the client's documented fallback
    /// from the list to the detail endpoint was reading a field that did not exist. It now reports
    /// the same seat count as the list, from the same definition.
    /// </summary>
    [Fact]
    public async Task RoomDetail_ReportsTheSameSeatCountAsTheList()
    {
        var room = await SeedRoomAsync(
            TranslationRoomParticipantStatuses.Connected,
            TranslationRoomParticipantStatuses.Connected,
            TranslationRoomParticipantStatuses.Connected,
            TranslationRoomParticipantStatuses.Kicked,
            TranslationRoomParticipantStatuses.Waiting);

        var detail = await _service.GetTranslationRoomAsync(room.Id);
        var listed = await ListRoomAsync(room.Id);

        detail.IsSuccess.Should().BeTrue(detail.Error);
        detail.Value!.ParticipantCount.Should().Be(3);
        detail.Value!.ParticipantCount.Should().Be(listed.ParticipantCount);
    }
}

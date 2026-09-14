using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// Who may edit a biên bản: the host, a workspace Owner/Admin, and the secretary the host names —
/// and nobody else, however much of the meeting they can read.
///
/// Against a real PostgreSQL for the same reason the revision tests are: the gate reads the
/// participant roster and the document together, and a mocked repository answers whatever the test
/// wrote rather than what the query would find.
/// </summary>
public sealed class MeetingMinutesSecretaryTests
    : IClassFixture<MeetingMinutesRevisionTests.Database>, IAsyncLifetime
{
    private static readonly Guid HostId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecretaryUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid AttendeeUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly MeetingMinutesRevisionTests.Database _database;
    private readonly Guid _workspaceId = Guid.NewGuid();

    private TranslationRoomDbContext _context = null!;
    private MeetingMinutesService _service = null!;

    public MeetingMinutesSecretaryTests(MeetingMinutesRevisionTests.Database database) => _database = database;

    public Task InitializeAsync()
    {
        _context = new TranslationRoomDbContext(
            new DbContextOptionsBuilder<TranslationRoomDbContext>()
                .UseNpgsql(_database.ConnectionString)
                .Options);

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
            new MeetingMinutesRepository(_context),
            new MeetingActionItemRepository(_context),
            new MeetingMinutesShareRepository(_context),
            new TranslationRoomSummaryVariantRepository(_context));

        // Only AdminId is a workspace Owner/Admin. Everyone else is answered false, which is what an
        // ordinary member gets from WorkspaceService.
        var directory = new Mock<IWorkspaceMemberDirectory>();
        directory
            .Setup(d => d.IsOwnerOrAdminAsync(It.IsAny<Guid>(), AdminId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _service = new MeetingMinutesService(
            unitOfWork,
            directory.Object,
            new Mock<IMeetingMinutesDocumentWriter>().Object,
            new Mock<ILogger<MeetingMinutesService>>().Object);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private sealed record Seeded(
        TranslationRoom Room,
        MeetingMinutes Minutes,
        TranslationRoomParticipant Secretary,
        TranslationRoomParticipant Attendee,
        TranslationRoomParticipant Guest);

    private async Task<Seeded> SeedDraftAsync()
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = _workspaceId,
            HostId = HostId,
            Title = "Weekly sync",
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
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

        TranslationRoomParticipant Person(Guid? userId, string name) => new()
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room.Id,
            UserId = userId,
            DisplayName = name,
            Role = "PARTICIPANT",
            ListenLanguage = "vi",
            SpeakLanguage = "vi",
            Status = "LEFT",
            ConnectionType = "WEB",
            JoinedAt = now.AddMinutes(-50),
            LeftAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var secretary = Person(SecretaryUserId, "Ngô Xuân Hạnh Nhi");
        var attendee = Person(AttendeeUserId, "Trần Mạnh Tuấn");
        // An external guest with no account: attended, but cannot be recognised by user id.
        var guest = Person(null, "Guest");

        var minutes = new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room.Id,
            WorkspaceId = _workspaceId,
            MinutesNo = $"BB-2026-{Random.Shared.Next(1000, 9999)}",
            Status = MeetingMinutesConstants.StatusDraft,
            Version = 1,
            IsCurrent = true,
            EditCountVsDraft = 0,
            Content = "{\"sections\":[]}",
            CreatedAt = now,
            CreatedBy = HostId,
            UpdatedAt = now,
            UpdatedBy = HostId
        };

        _context.TranslationRooms.Add(room);
        _context.TranslationRoomParticipants.AddRange(secretary, attendee, guest);
        _context.MeetingMinutes.Add(minutes);
        await _context.SaveChangesAsync();

        return new Seeded(room, minutes, secretary, attendee, guest);
    }

    private const string Edited = "{\"sections\":[],\"notes\":\"Corrected by hand\"}";

    [Fact]
    public async Task The_host_names_a_secretary_who_can_then_edit_and_sign()
    {
        var s = await SeedDraftAsync();

        var named = await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Secretary.Id);
        named.IsSuccess.Should().BeTrue(named.Error);
        named.Value!.SecretaryParticipantId.Should().Be(s.Secretary.Id);

        var saved = await _service.UpdateContentAsync(s.Room.Id, s.Minutes.Id, SecretaryUserId, Edited);
        saved.IsSuccess.Should().BeTrue(saved.Error);
        saved.Value!.CanEdit.Should().BeTrue("the save response replaces the cached copy the editor reads");
        saved.Value.CanApprove.Should().BeFalse();

        var signed = await _service.SignAsync(s.Room.Id, s.Minutes.Id, SecretaryUserId);
        signed.IsSuccess.Should().BeTrue(signed.Error);
        signed.Value!.Status.Should().Be(MeetingMinutesConstants.StatusInReview);
    }

    [Fact]
    public async Task An_attendee_who_was_not_named_cannot_edit()
    {
        var s = await SeedDraftAsync();
        await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Secretary.Id);

        var result = await _service.UpdateContentAsync(s.Room.Id, s.Minutes.Id, AttendeeUserId, Edited);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task A_workspace_admin_edits_without_being_host_or_secretary()
    {
        var s = await SeedDraftAsync();

        var result = await _service.UpdateContentAsync(s.Room.Id, s.Minutes.Id, AdminId, Edited);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.CanApprove.Should().BeTrue("an admin carries the host's authority");
    }

    [Fact]
    public async Task The_secretary_reads_the_draft_and_other_attendees_still_do_not()
    {
        var s = await SeedDraftAsync();
        await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Secretary.Id);

        var secretaryRead = await _service.GetCurrentAsync(s.Room.Id, SecretaryUserId, null);
        secretaryRead.IsSuccess.Should().BeTrue(secretaryRead.Error);
        secretaryRead.Value!.CanEdit.Should().BeTrue();
        secretaryRead.Value.CanDesignateSecretary.Should().BeFalse();

        var attendeeRead = await _service.GetCurrentAsync(s.Room.Id, AttendeeUserId, null);
        attendeeRead.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Only_host_authority_names_the_secretary()
    {
        var s = await SeedDraftAsync();
        await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Secretary.Id);

        var result = await _service.DesignateSecretaryAsync(
            s.Room.Id, s.Minutes.Id, SecretaryUserId, s.Attendee.Id);

        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Somebody_without_an_account_cannot_be_named()
    {
        var s = await SeedDraftAsync();

        var result = await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Guest.Id);

        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }

    [Fact]
    public async Task The_secretary_cannot_be_changed_once_the_minutes_are_signed()
    {
        var s = await SeedDraftAsync();
        await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Secretary.Id);
        await _service.SignAsync(s.Room.Id, s.Minutes.Id, SecretaryUserId);

        var result = await _service.DesignateSecretaryAsync(s.Room.Id, s.Minutes.Id, HostId, s.Attendee.Id);

        result.ErrorCode.Should().Be(ErrorCodes.InvalidState);
    }
}

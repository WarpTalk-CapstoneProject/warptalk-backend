using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Testcontainers.PostgreSql;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Infrastructure.Persistence;
using WarpTalk.TranslationRoomService.Infrastructure.Repositories;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Infrastructure;

/// <summary>
/// "Action: …, owner: me, deadline: none" becoming a row, against a real PostgreSQL.
///
/// The defect: WarpBot acknowledged a dictated action item with a tidy list and, asked where it
/// was saved, admitted it was saved nowhere. There was no endpoint to save it with — action items
/// were created only by approving minutes, and source_minutes_id was NOT NULL. These pin the new
/// path's rules: the caller is resolved without guessing, a name is resolved or refused, a missing
/// deadline stays missing, and a reader of the meeting is the only one who may add to it.
/// </summary>
public sealed class AssistantActionItemCreateTests
    : IClassFixture<AssistantActionItemCreateTests.Database>, IAsyncLifetime
{
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

    private static readonly Guid HostId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Database _database;
    private readonly Guid _workspaceId = Guid.NewGuid();

    private TranslationRoomDbContext _context = null!;
    private MeetingActionItemService _service = null!;

    public AssistantActionItemCreateTests(Database database) => _database = database;

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

        _service = new MeetingActionItemService(
            unitOfWork,
            new Mock<IWorkspaceMemberDirectory>().Object,
            new Mock<ILogger<MeetingActionItemService>>().Object);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task<TranslationRoom> SeedLiveRoomAsync(params (Guid? UserId, string Name)[] participants)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = _workspaceId,
            HostId = HostId,
            Title = "Weekly sync",
            TranslationRoomCode = Guid.NewGuid().ToString("N")[..12],
            Status = "IN_PROGRESS",
            TranslationRoomType = "STANDARD",
            MaxParticipants = 10,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            Settings = "{}",
            IsActive = true,
            StartedAt = now.AddMinutes(-20),
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.TranslationRooms.Add(room);

        foreach (var (userId, name) in participants)
        {
            _context.TranslationRoomParticipants.Add(new TranslationRoomParticipant
            {
                Id = Guid.CreateVersion7(),
                TranslationRoomId = room.Id,
                UserId = userId,
                DisplayName = name,
                Role = "PARTICIPANT",
                ListenLanguage = "vi",
                SpeakLanguage = "vi",
                Status = "CONNECTED",
                ConnectionType = "WEB",
                JoinedAt = now.AddMinutes(-10),
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-10)
            });
        }

        await _context.SaveChangesAsync();
        return room;
    }

    [Fact]
    public async Task Owner_me_assigns_the_caller_and_records_the_name_the_room_knows_them_by()
    {
        var callerId = Guid.NewGuid();
        var room = await SeedLiveRoomAsync((HostId, "Huỳnh Thái Tú"), (callerId, "Ngô Xuân Hạnh Nhi"));

        var result = await _service.CreateAsync(
            room.Id,
            callerId,
            null,
            new CreateActionItemRequest(
                "Hỏi người phụ trách để xác nhận thông tin cần thiết",
                AssignToSelf: true));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.AssigneeUserId.Should().Be(callerId);
        result.Value.OwnerName.Should().Be("Ngô Xuân Hạnh Nhi");
        result.Value.Status.Should().Be(MeetingActionItemConstants.StatusOpen);
        result.Value.Source.Should().Be(MeetingActionItemConstants.SourceAssistant);
        result.Value.SourceMinutesId.Should().BeNull();
        // "Deadline: chưa xác định" is an answer, and it is stored as no date — never as today.
        result.Value.DueDate.Should().BeNull();
        result.Value.RoomTitle.Should().Be("Weekly sync");

        _context.ChangeTracker.Clear();
        var row = await _context.MeetingActionItems.SingleAsync(item => item.Id == result.Value.Id);
        row.CreatedBy.Should().Be(callerId);
        row.Source.Should().Be(MeetingActionItemConstants.SourceAssistant);
    }

    [Fact]
    public async Task The_new_task_is_in_the_callers_own_task_list()
    {
        var callerId = Guid.NewGuid();
        var room = await SeedLiveRoomAsync((callerId, "Trần Mạnh Tuấn"));

        var created = await _service.CreateAsync(
            room.Id, callerId, null,
            new CreateActionItemRequest("Send the deck", AssignToSelf: true, DueDate: new DateOnly(2026, 10, 1)));
        created.IsSuccess.Should().BeTrue(created.Error);

        var mine = await _service.GetMineAsync(_workspaceId, callerId, null);

        mine.IsSuccess.Should().BeTrue();
        mine.Value!.Should().ContainSingle(item => item.Id == created.Value!.Id)
            .Which.DueDate.Should().Be(new DateOnly(2026, 10, 1));
    }

    [Fact]
    public async Task A_named_owner_resolves_against_the_roster()
    {
        var nhi = Guid.NewGuid();
        var room = await SeedLiveRoomAsync((HostId, "Huỳnh Thái Tú"), (nhi, "Ngô Xuân Hạnh Nhi"));

        var result = await _service.CreateAsync(
            room.Id, HostId, null, new CreateActionItemRequest("Draft the release note", OwnerName: "chị Nhi"));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.AssigneeUserId.Should().Be(nhi);
        // What was said is kept as it was said.
        result.Value.OwnerName.Should().Be("chị Nhi");
    }

    [Fact]
    public async Task A_name_that_matches_nobody_is_refused_rather_than_stored_unassigned()
    {
        var room = await SeedLiveRoomAsync((HostId, "Huỳnh Thái Tú"));

        var result = await _service.CreateAsync(
            room.Id, HostId, null, new CreateActionItemRequest("Book the venue", OwnerName: "Kỳ"));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        (await _context.MeetingActionItems.CountAsync(item => item.TranslationRoomId == room.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task No_owner_at_all_is_an_unassigned_task()
    {
        var room = await SeedLiveRoomAsync((HostId, "Huỳnh Thái Tú"));

        var result = await _service.CreateAsync(
            room.Id, HostId, null, new CreateActionItemRequest("Decide on the vendor"));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.AssigneeUserId.Should().BeNull();
        result.Value.OwnerName.Should().BeNull();
    }

    [Fact]
    public async Task Somebody_who_cannot_read_the_meeting_cannot_add_to_it()
    {
        var room = await SeedLiveRoomAsync((HostId, "Huỳnh Thái Tú"));

        var result = await _service.CreateAsync(
            room.Id, Guid.NewGuid(), "stranger@example.com",
            new CreateActionItemRequest("Anything", AssignToSelf: true));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task An_empty_task_is_refused()
    {
        var room = await SeedLiveRoomAsync((HostId, "Huỳnh Thái Tú"));

        var result = await _service.CreateAsync(
            room.Id, HostId, null, new CreateActionItemRequest("   ", AssignToSelf: true));

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
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
/// WT-703 on the biên bản: reading or exporting the record in a language it does not store goes
/// through the summary rendering, and a language the meeting does not offer must come back as a
/// refusal of the request (ValidationError, 400) — not be dressed up as "unavailable" and not be
/// silently exported in another language. A translation the record already stores is content
/// that exists, and stays readable whatever the meeting's languages are now.
///
/// Against a real PostgreSQL because GetCurrentAsync's read gate is an EF query; the rendering
/// itself is mocked at its interface, which is the seam the minutes service depends on.
/// </summary>
public sealed class MeetingMinutesTranslationLanguageTests
    : IClassFixture<MeetingMinutesTranslationLanguageTests.Database>, IAsyncLifetime
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

    private const string RefusedLanguage = "fr";

    private readonly Database _database;
    private readonly Guid _workspaceId = Guid.NewGuid();

    private TranslationRoomDbContext _context = null!;
    private MeetingMinutesService _service = null!;
    private Mock<ITranslationRoomArtifactService> _summaryVariants = null!;
    private Mock<IMeetingMinutesDocumentWriter> _documentWriter = null!;

    public MeetingMinutesTranslationLanguageTests(Database database) => _database = database;

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

        // What the artifact service answers once the room's language policy refuses a language.
        _summaryVariants = new Mock<ITranslationRoomArtifactService>();
        _summaryVariants
            .Setup(item => item.GetOrQueueSummaryVariantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), RefusedLanguage,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<SummaryVariantDto>(
                "Language 'fr' is not allowed for this meeting's artifacts. Allowed languages: vi, en.",
                ErrorCodes.ValidationError));

        _documentWriter = new Mock<IMeetingMinutesDocumentWriter>();
        _documentWriter
            .Setup(item => item.WriteDocx(
                It.IsAny<MeetingMinutesDto>(), It.IsAny<MeetingMinutesContent>(), It.IsAny<string>()))
            .Returns(new byte[] { 1, 2, 3 });

        _service = new MeetingMinutesService(
            unitOfWork,
            // Never consulted: the caller is the host, and the read gate admits them first.
            new Mock<IWorkspaceMemberDirectory>().Object,
            _documentWriter.Object,
            new Mock<ILogger<MeetingMinutesService>>().Object,
            summaryVariants: _summaryVariants.Object);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    [Fact]
    public async Task A_language_the_meeting_does_not_offer_is_refused_as_a_bad_request()
    {
        var room = await SeedRoomWithApprovedMinutesAsync();

        var result = await _service.GetTranslationAsync(room.Id, HostId, null, RefusedLanguage, "Bearer t");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError,
            "a refused language is a refusal of the request, not an unavailable document");
    }

    [Fact]
    public async Task A_translation_the_record_already_stores_is_still_read_without_generating()
    {
        var room = await SeedRoomWithApprovedMinutesAsync(storedLanguage: RefusedLanguage);

        var result = await _service.GetTranslationAsync(room.Id, HostId, null, RefusedLanguage, "Bearer t");

        result.IsSuccess.Should().BeTrue("error was: {0}", result.Error);
        result.Value!.Status.Should().Be(MinutesTranslationStatus.Ready);
        result.Value.Sections.Should().NotBeNullOrEmpty();
        _summaryVariants.Verify(
            item => item.GetOrQueueSummaryVariantAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A rendering that FAILED is an answer. This used to come back as "generating", so the web
    /// kept polling and every other poll re-queued the job — production shows one failing request
    /// every eight seconds on one room on 12 Sep — and the reader finally saw "has not arrived"
    /// with the reason thrown away.
    /// </summary>
    [Fact]
    public async Task A_failed_rendering_is_reported_with_its_reason_instead_of_generating_forever()
    {
        var room = await SeedRoomWithApprovedMinutesAsync();
        _summaryVariants
            .Setup(item => item.GetOrQueueSummaryVariantAsync(
                room.Id, HostId, It.IsAny<string>(), "en", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SummaryVariantDto>.Success(new SummaryVariantDto(
                "general", "en", null, false, SummaryVariantStatus.Failed, null, "Could not read the transcript.")));

        var result = await _service.GetTranslationAsync(room.Id, HostId, null, "en", "Bearer t");

        result.IsSuccess.Should().BeTrue("error was: {0}", result.Error);
        result.Value!.Status.Should().Be(MinutesTranslationStatus.Unavailable);
        result.Value.UnavailableReason.Should().Be("Could not read the transcript.");
    }

    /// <summary>
    /// The language switch end to end on the minutes side: a translated rendering of the summary
    /// the document was drawn from carries the same section keys, so it is served as the record
    /// in that language.
    /// </summary>
    [Fact]
    public async Task A_translation_of_the_summary_the_record_was_drawn_from_is_served()
    {
        var room = await SeedRoomWithApprovedMinutesAsync();
        _summaryVariants
            .Setup(item => item.GetOrQueueSummaryVariantAsync(
                room.Id, HostId, "general", "en", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SummaryVariantDto>.Success(new SummaryVariantDto(
                "general",
                "en",
                "{\"summary\":\"The board met.\",\"templateKey\":\"general\",\"summaryLanguage\":\"en\"}",
                false,
                SummaryVariantStatus.Ready,
                DateTime.UtcNow)));

        var result = await _service.GetTranslationAsync(room.Id, HostId, null, "en-US", "Bearer t");

        result.IsSuccess.Should().BeTrue("error was: {0}", result.Error);
        result.Value!.Status.Should().Be(MinutesTranslationStatus.Ready);
        result.Value.Language.Should().Be("en");
        result.Value.Sections.Should().ContainSingle(section => section.Key == "summary" && section.Text == "The board met.");
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("pdf")]
    public async Task Exporting_in_a_language_the_meeting_does_not_offer_is_refused_too(string format)
    {
        var room = await SeedRoomWithApprovedMinutesAsync();

        var result = await _service.ExportAsync(
            room.Id, HostId, null, template: null, format, language: RefusedLanguage, bearerToken: "Bearer t");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationError);
        _documentWriter.Verify(
            item => item.WriteDocx(
                It.IsAny<MeetingMinutesDto>(), It.IsAny<MeetingMinutesContent>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task Exporting_a_stored_translation_still_produces_the_file()
    {
        var room = await SeedRoomWithApprovedMinutesAsync(storedLanguage: RefusedLanguage);

        var result = await _service.ExportAsync(
            room.Id, HostId, null, template: null, "docx", language: RefusedLanguage, bearerToken: "Bearer t");

        result.IsSuccess.Should().BeTrue("error was: {0}", result.Error);
        result.Value!.Bytes.Should().NotBeEmpty();
    }

    private async Task<TranslationRoom> SeedRoomWithApprovedMinutesAsync(string? storedLanguage = null)
    {
        var now = DateTime.UtcNow;
        var room = new TranslationRoom
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = _workspaceId,
            HostId = HostId,
            Title = "Board meeting",
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
        _context.TranslationRooms.Add(room);

        var translations = storedLanguage == null
            ? string.Empty
            : ",\"translations\":{\"" + storedLanguage
                + "\":[{\"key\":\"summary\",\"kind\":\"paragraph\",\"text\":\"Résumé\"}]}";

        _context.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.CreateVersion7(),
            TranslationRoomId = room.Id,
            WorkspaceId = _workspaceId,
            MinutesNo = "BB-2026-" + Random.Shared.Next(1000, 9999),
            Status = MeetingMinutesConstants.StatusApproved,
            Version = 1,
            IsCurrent = true,
            SecretarySignedAt = now.AddMinutes(-10),
            ChairApprovedAt = now.AddMinutes(-5),
            // Unedited, so a language the record does not store goes on to the rendering.
            EditCountVsDraft = 0,
            Content = "{\"primaryLanguage\":\"vi\",\"sections\":[{\"key\":\"summary\",\"kind\":\"paragraph\",\"text\":\"Tóm tắt\"}]"
                + translations + "}",
            CreatedAt = now.AddMinutes(-20),
            CreatedBy = HostId,
            UpdatedAt = now.AddMinutes(-5),
            UpdatedBy = HostId
        });

        await _context.SaveChangesAsync();
        return room;
    }
}

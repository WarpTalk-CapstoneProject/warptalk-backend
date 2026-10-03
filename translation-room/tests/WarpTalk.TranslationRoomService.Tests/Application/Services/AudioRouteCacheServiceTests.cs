using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class AudioRouteCacheServiceTests
{
    private readonly Mock<ITranslationRoomAudioRouteRepository> _mockRouteRepository;
    private readonly Mock<ITranslationRoomRepository> _mockRoomRepository;
    private readonly Mock<ITranslationRoomSessionRepository> _mockSessionRepository;
    private readonly Mock<IRedisStateRepository> _mockRedisStateRepo;
    private readonly Mock<IDubVoiceDirectory> _mockDubVoices;
    private readonly AudioRouteCacheService _service;

    public AudioRouteCacheServiceTests()
    {
        _mockRouteRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        _mockRoomRepository = new Mock<ITranslationRoomRepository>();
        _mockSessionRepository = new Mock<ITranslationRoomSessionRepository>();
        _mockRedisStateRepo = new Mock<IRedisStateRepository>();
        // Silent by default: WT-396's enrichment is pinned in DubVoicePublishedTests, and a
        // directory that answered here would change what every assertion below is testing.
        _mockDubVoices = new Mock<IDubVoiceDirectory>();
        _service = new AudioRouteCacheService(
            _mockRouteRepository.Object,
            _mockRoomRepository.Object,
            _mockSessionRepository.Object,
            _mockRedisStateRepo.Object,
            _mockDubVoices.Object);
    }

    /// <summary>
    /// The AI workers read this flag to decide whether to translate, and it must not be inferable
    /// from the room's status: a room is IN_PROGRESS from the moment it is opened, which is
    /// exactly the state in which nobody has started translation yet.
    /// </summary>
    [Fact]
    public async Task PublishRoutesUpdateAsync_ReportsTranslationInactive_ForALiveRoomWithNoSession()
    {
        var roomId = Guid.NewGuid();
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        _mockRoomRepository.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = roomId, Status = "IN_PROGRESS" });
        _mockSessionRepository.Setup(r => r.GetActiveSessionByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomSession?)null);

        string? published = null;
        _mockRedisStateRepo
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, payload) => published = payload)
            .ReturnsAsync(1L);

        await _service.PublishRoutesUpdateAsync(roomId, CancellationToken.None);

        using var document = JsonDocument.Parse(published!);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("room_status").GetString().Should().Be("IN_PROGRESS");
        data.GetProperty("translation_active").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PublishRoutesUpdateAsync_ReportsTranslationActive_WhileASessionIsOpen()
    {
        var roomId = Guid.NewGuid();
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>());
        _mockRoomRepository.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = roomId, Status = "IN_PROGRESS" });
        _mockSessionRepository.Setup(r => r.GetActiveSessionByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoomSession { Id = Guid.NewGuid(), TranslationRoomId = roomId });

        string? published = null;
        _mockRedisStateRepo
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, payload) => published = payload)
            .ReturnsAsync(1L);

        await _service.PublishRoutesUpdateAsync(roomId, CancellationToken.None);

        using var document = JsonDocument.Parse(published!);
        document.RootElement.GetProperty("data").GetProperty("translation_active").GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public async Task PublishRoutesUpdateAsync_ShouldSerializeAndPublishCorrectPayload()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routes = new List<TranslationRoomAudioRoute>
        {
            new TranslationRoomAudioRoute
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = roomId,
                SourceParticipantId = Guid.NewGuid(),
                TargetParticipantId = Guid.NewGuid(),
                SourceLanguage = "en",
                TargetLanguage = "vi",
                VoiceCloneEnabled = true,
                Status = "READY".ToString()
            }
        };

        var room = new TranslationRoom
        {
            Id = roomId,
            Status = "IN_PROGRESS"
        };

        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(routes);

        _mockRoomRepository.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        // Act
        var result = await _service.PublishRoutesUpdateAsync(roomId, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Status.Should().Be("READY");

        _mockRedisStateRepo.Verify(r => r.StringSetAsync(
            It.Is<string>(k => k == $"translationRoom:{roomId}:audio_routes"),
            It.IsAny<string>(),
            It.Is<TimeSpan>(t => t == TimeSpan.FromHours(12))), Times.Once);

        _mockRedisStateRepo.Verify(r => r.PublishAsync(
            It.Is<string>(c => c == $"translationRoom:{roomId}:events"),
            It.Is<string>(p => p.Contains("AUDIO_ROUTES_UPDATED"))), Times.Once);
    }

    // ── EXTERNAL_BRIDGE: the far side's language is a language of the room ───────────────────

    private static readonly Guid StandIn = new("00000000-0000-0000-0000-00000000b21d");

    private async Task<List<string>> PublishedRoomLanguagesAsync(string roomType, string farSideLanguage)
    {
        var roomId = Guid.NewGuid();
        var host = new TranslationRoomParticipant { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        var standIn = new TranslationRoomParticipant { Id = Guid.NewGuid(), UserId = StandIn };
        _mockDubVoices.Setup(d => d.GetSelectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DubVoiceSelection(null, null, null));
        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute>
            {
                new()
                {
                    Id = Guid.NewGuid(), TranslationRoomId = roomId,
                    SourceParticipantId = standIn.Id, SourceParticipant = standIn,
                    TargetParticipantId = host.Id, TargetParticipant = host,
                    SourceLanguage = farSideLanguage, TargetLanguage = "en", Status = "PENDING",
                },
                new()
                {
                    Id = Guid.NewGuid(), TranslationRoomId = roomId,
                    SourceParticipantId = host.Id, SourceParticipant = host,
                    TargetParticipantId = standIn.Id, TargetParticipant = standIn,
                    SourceLanguage = "en", TargetLanguage = farSideLanguage, Status = "PENDING",
                },
            });
        _mockRoomRepository.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = roomId,
                Status = "IN_PROGRESS",
                TranslationRoomType = roomType,
                SourceLanguage = "en",
                TargetLanguages = """["vi","en"]""",
            });

        string? published = null;
        _mockRedisStateRepo
            .Setup(r => r.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, payload) => published = payload)
            .ReturnsAsync(1L);

        await _service.PublishRoutesUpdateAsync(roomId, CancellationToken.None);

        using var document = JsonDocument.Parse(published!);
        var languages = new List<string>();
        foreach (var element in document.RootElement.GetProperty("data").GetProperty("room_languages").EnumerateArray())
            languages.Add(element.GetString()!);
        return languages;
    }

    /// <summary>
    /// "They speak" moves the stand-in to a language the bridge room never declared (a bridge has
    /// no L2). STT allows speak_languages ∪ room_languages, and while the far side runs unpinned
    /// ("auto", WT-909 wave 2) only room_languages can name it — without this STT deleted every
    /// line the Meet side spoke in the language it was just declared to speak.
    /// </summary>
    [Fact]
    public async Task PublishRoutesUpdateAsync_BridgeRoom_DeclaresTheFarSidesCurrentLanguage()
    {
        var languages = await PublishedRoomLanguagesAsync("EXTERNAL_BRIDGE", "ja-JP");

        languages.Should().BeEquivalentTo(new[] { "en", "vi", "ja" });
    }

    [Fact]
    public async Task PublishRoutesUpdateAsync_BridgeRoom_DoesNotRepeatADeclaredFarSideLanguage()
    {
        var languages = await PublishedRoomLanguagesAsync("EXTERNAL_BRIDGE", "vi");

        languages.Should().Equal("en", "vi");
    }

    /// <summary>A native room's allow-list stays exactly what the room was configured for.</summary>
    [Fact]
    public async Task PublishRoutesUpdateAsync_NativeRoom_KeepsRoomLanguagesToItsConfiguration()
    {
        var languages = await PublishedRoomLanguagesAsync("MEETING", "ja");

        languages.Should().Equal("en", "vi");
    }
}

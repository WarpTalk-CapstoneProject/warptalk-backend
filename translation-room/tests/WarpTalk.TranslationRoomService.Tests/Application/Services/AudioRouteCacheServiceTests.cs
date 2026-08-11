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
    private readonly AudioRouteCacheService _service;

    public AudioRouteCacheServiceTests()
    {
        _mockRouteRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        _mockRoomRepository = new Mock<ITranslationRoomRepository>();
        _mockSessionRepository = new Mock<ITranslationRoomSessionRepository>();
        _mockRedisStateRepo = new Mock<IRedisStateRepository>();
        _service = new AudioRouteCacheService(
            _mockRouteRepository.Object,
            _mockRoomRepository.Object,
            _mockSessionRepository.Object,
            _mockRedisStateRepo.Object);
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
}

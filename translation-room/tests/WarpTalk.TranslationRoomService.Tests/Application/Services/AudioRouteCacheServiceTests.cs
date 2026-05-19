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
    private readonly Mock<IRedisStateRepository> _mockRedisStateRepo;
    private readonly AudioRouteCacheService _service;

    public AudioRouteCacheServiceTests()
    {
        _mockRouteRepository = new Mock<ITranslationRoomAudioRouteRepository>();
        _mockRoomRepository = new Mock<ITranslationRoomRepository>();
        _mockRedisStateRepo = new Mock<IRedisStateRepository>();
        _service = new AudioRouteCacheService(
            _mockRouteRepository.Object,
            _mockRoomRepository.Object,
            _mockRedisStateRepo.Object);
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
                Status = AudioRouteStatus.ROUTING_READY.ToString()
            }
        };

        var room = new TranslationRoom
        {
            Id = roomId,
            Status = RoomStatus.IN_PROGRESS.ToString()
        };

        _mockRouteRepository.Setup(r => r.GetRoutesByRoomIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(routes);

        _mockRoomRepository.Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        // Act
        var result = await _service.PublishRoutesUpdateAsync(roomId, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result[0].Status.Should().Be(AudioRouteStatus.ROUTING_READY);

        _mockRedisStateRepo.Verify(r => r.StringSetAsync(
            It.Is<string>(k => k == $"translationRoom:{roomId}:audio_routes"),
            It.IsAny<string>(),
            It.Is<TimeSpan>(t => t == TimeSpan.FromHours(12))), Times.Once);

        _mockRedisStateRepo.Verify(r => r.PublishAsync(
            It.Is<string>(c => c == $"translationRoom:{roomId}:events"),
            It.Is<string>(p => p.Contains("AUDIO_ROUTES_UPDATED"))), Times.Once);
    }
}

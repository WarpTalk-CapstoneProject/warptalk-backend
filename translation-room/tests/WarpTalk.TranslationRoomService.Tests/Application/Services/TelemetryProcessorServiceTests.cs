using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Application.BackgroundProcessors;
using WarpTalk.TranslationRoomService.Domain.Enums;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class TelemetryProcessorServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis;
    private readonly Mock<IDatabase> _mockDb;
    private readonly Mock<IAudioRouteEventProcessorService> _mockEventProcessor;
    private readonly Mock<ILogger<TelemetryProcessorService>> _mockLogger;
    private readonly TelemetryProcessorService _service;

    public TelemetryProcessorServiceTests()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDb = new Mock<IDatabase>();
        _mockEventProcessor = new Mock<IAudioRouteEventProcessorService>();
        _mockLogger = new Mock<ILogger<TelemetryProcessorService>>();

        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDb.Object);

        _service = new TelemetryProcessorService(
            _mockRedis.Object,
            _mockEventProcessor.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldBypassAndIncrementWarmup_WhenWarmupCountIsLessThanThree()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "stt",
            LatencyMs = 5000.0, // High latency that would otherwise trigger degradation
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var hashEntries = new HashEntry[]
        {
            new HashEntry("warmup_count", 1),
            new HashEntry("last_timestamp", payload.Timestamp - 1000)
        };

        _mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        // Act
        var result = await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        
        // Assert warmup_count increments to 2
        _mockDb.Verify(d => d.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.Is<HashEntry[]>(entries => 
                entries[0].Name == "warmup_count" && (int)entries[0].Value == 2 &&
                entries[1].Name == "last_timestamp" && (long)entries[1].Value == payload.Timestamp),
            It.IsAny<CommandFlags>()), Times.Once);

        // Assert no event is processed due to warm-up protective bypass
        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldCalculateSttEmaAndTriggerDegradation_WhenWarmupIsCompleteAndSttLatencyExceedsThreshold()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "stt",
            LatencyMs = 4000.0, // High latency above 3000ms threshold
            Timestamp = timestamp
        };

        // Warmup count is 3 (warmup complete), STT is not currently degraded
        var hashEntries = new HashEntry[]
        {
            new HashEntry("warmup_count", 3),
            new HashEntry("stt_ema", 2000.0),
            new HashEntry("is_stt_degraded", false),
            new HashEntry("last_timestamp", timestamp - 1000) // 1 second gap -> alpha = 0.3
        };

        _mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.stt_or_translation_latency_high.ToString(), "{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        var result = await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // EMA computation check:
        // alpha = 0.1 + (0.2 * 1.0) = 0.3
        // New EMA = (4000.0 * 0.3) + (2000.0 * 0.7) = 1200 + 1400 = 2600.
        // Wait, is 2600 > 3000? No! So it should update EMA but NOT degrade yet.
        _mockDb.Verify(d => d.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.Is<HashEntry[]>(entries => 
                HasEntryWithNameAndValue(entries, "stt_ema", 2600.0) &&
                !HasEntryWithNameAndValue(entries, "is_stt_degraded", true)),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.stt_or_translation_latency_high.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldDegradeSttRoute_WhenEmaActuallyCrossesThreshold()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "stt",
            LatencyMs = 6000.0, // High latency
            Timestamp = timestamp
        };

        var hashEntries = new HashEntry[]
        {
            new HashEntry("warmup_count", 3),
            new HashEntry("stt_ema", 2800.0),
            new HashEntry("is_stt_degraded", false),
            new HashEntry("last_timestamp", timestamp - 1000) // alpha = 0.3
        };

        _mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        // Act
        var result = await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // EMA computation:
        // New EMA = (6000 * 0.3) + (2800 * 0.7) = 1800 + 1960 = 3760.
        // 3760 > 3000. So it degrades.
        _mockDb.Verify(d => d.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.Is<HashEntry[]>(entries => 
                HasEntryWithNameAndValue(entries, "stt_ema", 3760.0) &&
                HasEntryWithNameAndValue(entries, "is_stt_degraded", true)),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.stt_or_translation_latency_high.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldRecoverSttRoute_WhenEmaFallsBelowRecoveryThreshold()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "stt",
            LatencyMs = 500.0, // Clean, fast latency
            Timestamp = timestamp
        };

        // Already degraded
        var hashEntries = new HashEntry[]
        {
            new HashEntry("warmup_count", 3),
            new HashEntry("stt_ema", 1800.0),
            new HashEntry("is_stt_degraded", true),
            new HashEntry("last_timestamp", timestamp - 1000) // alpha = 0.3
        };

        _mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        // Act
        var result = await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // EMA calculation:
        // New EMA = (500 * 0.3) + (1800 * 0.7) = 150 + 1260 = 1410.
        // 1410 < 1500 (STT recovery threshold). So it recovers!
        _mockDb.Verify(d => d.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.Is<HashEntry[]>(entries => 
                HasEntryWithNameAndValue(entries, "stt_ema", 1410.0) &&
                HasEntryWithNameAndValue(entries, "is_stt_degraded", false)),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.pipeline_recovered.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldDegradeTtsRoute_WhenTtsEmaCrossesDegradedThreshold()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "tts",
            LatencyMs = 10000.0, // Extremely high latency
            Timestamp = timestamp
        };

        var hashEntries = new HashEntry[]
        {
            new HashEntry("warmup_count", 3),
            new HashEntry("tts_ema", 5000.0),
            new HashEntry("is_tts_degraded", false),
            new HashEntry("last_timestamp", timestamp - 1000) // alpha = 0.3
        };

        _mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        // Act
        var result = await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // EMA calculation:
        // New EMA = (10000 * 0.3) + (5000 * 0.7) = 3000 + 3500 = 6500.
        // 6500 > 6000 (TTS degraded threshold). So it degrades!
        _mockDb.Verify(d => d.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.Is<HashEntry[]>(entries => 
                HasEntryWithNameAndValue(entries, "tts_ema", 6500.0) &&
                HasEntryWithNameAndValue(entries, "is_tts_degraded", true)),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.tts_latency_high.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldRecoverTtsRoute_WhenTtsEmaFallsBelowRecoveryThreshold()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "tts",
            LatencyMs = 1000.0,
            Timestamp = timestamp
        };

        // Currently degraded
        var hashEntries = new HashEntry[]
        {
            new HashEntry("warmup_count", 3),
            new HashEntry("tts_ema", 3500.0),
            new HashEntry("is_tts_degraded", true),
            new HashEntry("last_timestamp", timestamp - 1000) // alpha = 0.3
        };

        _mockDb.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(hashEntries);

        // Act
        var result = await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // EMA calculation:
        // New EMA = (1000 * 0.3) + (3500 * 0.7) = 300 + 2450 = 2750.
        // 2750 < 3000 (TTS recovery threshold). So it recovers!
        _mockDb.Verify(d => d.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.Is<HashEntry[]>(entries => 
                HasEntryWithNameAndValue(entries, "tts_ema", 2750.0) &&
                HasEntryWithNameAndValue(entries, "is_tts_degraded", false)),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.voice_recovered.ToString(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool HasEntryWithNameAndValue(HashEntry[] entries, string name, RedisValue value)
    {
        foreach (var entry in entries)
        {
            if (entry.Name == name)
            {
                if (double.TryParse((string?)entry.Value, out double entryD) && double.TryParse((string?)value, out double valD))
                {
                    return Math.Abs(entryD - valD) < 0.001;
                }
                return entry.Value == value;
            }
        }
        return false;
    }
}

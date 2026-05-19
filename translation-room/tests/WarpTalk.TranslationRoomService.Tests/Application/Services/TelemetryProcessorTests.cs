using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.Shared;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

public class TelemetryProcessorTests
{
    private readonly Mock<IRedisStateRepository> _mockRedisStateRepo;
    private readonly Mock<IAudioRouteEventProcessor> _mockEventProcessor;
    private readonly Mock<ILogger<TelemetryProcessor>> _mockLogger;
    private readonly Mock<IOptionsMonitor<TelemetrySettings>> _mockOptions;
    private readonly TelemetryProcessor _service;

    public TelemetryProcessorTests()
    {
        _mockRedisStateRepo = new Mock<IRedisStateRepository>();
        _mockEventProcessor = new Mock<IAudioRouteEventProcessor>();
        _mockLogger = new Mock<ILogger<TelemetryProcessor>>();
        _mockOptions = new Mock<IOptionsMonitor<TelemetrySettings>>();

        // Setup standard options
        _mockOptions.Setup(o => o.CurrentValue).Returns(new TelemetrySettings
        {
            SttDegradedMs = 3000.0,
            SttRecoveryMs = 1500.0,
            TranslationDegradedMs = 2500.0,
            TranslationRecoveryMs = 1200.0,
            TtsDegradedMs = 6000.0,
            TtsRecoveryMs = 3000.0,
            WarmupCount = 3
        });

        _service = new TelemetryProcessor(
            _mockRedisStateRepo.Object,
            _mockLogger.Object,
            _mockEventProcessor.Object,
            _mockOptions.Object);
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

        var state = new Dictionary<string, string>
        {
            { "warmup_count", "1" },
            { "last_timestamp", (payload.Timestamp - 1000).ToString() }
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        
        // Assert warmup_count increments to 2
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                d["warmup_count"] == "2" &&
                d["last_timestamp"] == payload.Timestamp.ToString())), Times.Once);

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
        var state = new Dictionary<string, string>
        {
            { "warmup_count", "3" },
            { "stt_ema", "2000.0" },
            { "is_stt_degraded", "False" },
            { "last_timestamp", (timestamp - 1000).ToString() } // 1 second gap -> alpha = 0.3
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"AUDIO_ROUTING_ACTIVE\"}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        // EMA computation check:
        // alpha = 0.1 + (0.2 * 1.0) = 0.3
        // New EMA = (4000.0 * 0.3) + (2000.0 * 0.7) = 1200 + 1400 = 2600.
        // 2600 is below 3000 degraded threshold, so no event should fire
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                HasKeyAndValue(d, "stt_ema", 2600.0) &&
                !HasKeyAndBoolValue(d, "is_stt_degraded", true))), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"AUDIO_ROUTING_ACTIVE\"}", It.IsAny<CancellationToken>()), Times.Once);
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

        var state = new Dictionary<string, string>
        {
            { "warmup_count", "3" },
            { "stt_ema", "2800.0" },
            { "is_stt_degraded", "False" },
            { "last_timestamp", (timestamp - 1000).ToString() } // alpha = 0.3
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"STT_DEGRADED\"}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        // EMA computation:
        // New EMA = (6000 * 0.3) + (2800 * 0.7) = 1800 + 1960 = 3760.
        // 3760 > 3000. So it degrades.
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                HasKeyAndValue(d, "stt_ema", 3760.0) &&
                HasKeyAndBoolValue(d, "is_stt_degraded", true))), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"STT_DEGRADED\"}", It.IsAny<CancellationToken>()), Times.Once);
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
        var state = new Dictionary<string, string>
        {
            { "warmup_count", "3" },
            { "stt_ema", "1800.0" },
            { "is_stt_degraded", "True" },
            { "last_timestamp", (timestamp - 1000).ToString() } // alpha = 0.3
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"AUDIO_ROUTING_ACTIVE\"}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        // EMA calculation:
        // New EMA = (500 * 0.3) + (1800 * 0.7) = 150 + 1260 = 1410.
        // 1410 < 1500 (STT recovery threshold). So it recovers!
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                HasKeyAndValue(d, "stt_ema", 1410.0) &&
                HasKeyAndBoolValue(d, "is_stt_degraded", false))), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"AUDIO_ROUTING_ACTIVE\"}", It.IsAny<CancellationToken>()), Times.Once);
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

        var state = new Dictionary<string, string>
        {
            { "warmup_count", "3" },
            { "tts_ema", "5000.0" },
            { "is_tts_degraded", "False" },
            { "last_timestamp", (timestamp - 1000).ToString() } // alpha = 0.3
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"TTS_DEGRADED\"}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        // EMA calculation:
        // New EMA = (10000 * 0.3) + (5000 * 0.7) = 3000 + 3500 = 6500.
        // 6500 > 6000 (TTS degraded threshold). So it degrades!
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                HasKeyAndValue(d, "tts_ema", 6500.0) &&
                HasKeyAndBoolValue(d, "is_tts_degraded", true))), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"TTS_DEGRADED\"}", It.IsAny<CancellationToken>()), Times.Once);
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
        var state = new Dictionary<string, string>
        {
            { "warmup_count", "3" },
            { "tts_ema", "3500.0" },
            { "is_tts_degraded", "True" },
            { "last_timestamp", (timestamp - 1000).ToString() } // alpha = 0.3
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"AUDIO_ROUTING_ACTIVE\"}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        // EMA calculation:
        // New EMA = (1000 * 0.3) + (3500 * 0.7) = 300 + 2450 = 2750.
        // 2750 < 3000 (TTS recovery threshold). So it recovers!
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                HasKeyAndValue(d, "tts_ema", 2750.0) &&
                HasKeyAndBoolValue(d, "is_tts_degraded", false))), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"AUDIO_ROUTING_ACTIVE\"}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTelemetryAsync_ShouldDegradeTranslationRoute_WhenTranslationEmaCrossesDegradedThreshold()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var payload = new TelemetryPayload
        {
            RoomId = roomId,
            RouteId = routeId,
            WorkerType = "translation",
            LatencyMs = 4000.0, // High translation latency
            Timestamp = timestamp
        };

        var state = new Dictionary<string, string>
        {
            { "warmup_count", "3" },
            { "translation_ema", "2000.0" },
            { "is_translation_degraded", "False" },
            { "last_timestamp", (timestamp - 1000).ToString() } // alpha = 0.3
        };

        _mockRedisStateRepo.Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(state);

        _mockEventProcessor.Setup(e => e.ProcessEventAsync(roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"TRANSLATION_DEGRADED\"}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        // Act
        Func<Task> act = async () => await _service.ProcessTelemetryAsync(payload, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        // EMA calculation:
        // New EMA = (4000 * 0.3) + (2000 * 0.7) = 1200 + 1400 = 2600.
        // 2600 > 2500 (Translation degraded threshold). So it degrades!
        _mockRedisStateRepo.Verify(r => r.HashSetAsync(
            It.IsAny<string>(),
            It.Is<Dictionary<string, string>>(d => 
                HasKeyAndValue(d, "translation_ema", 2600.0) &&
                HasKeyAndBoolValue(d, "is_translation_degraded", true))), Times.Once);

        _mockEventProcessor.Verify(e => e.ProcessEventAsync(
            roomId, routeId, AudioRoutingEventType.telemetry_state_updated.ToString(), "{\"status\":\"TRANSLATION_DEGRADED\"}", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool HasKeyAndValue(Dictionary<string, string> dict, string key, double value)
    {
        if (dict.TryGetValue(key, out var valStr) && double.TryParse(valStr, out var val))
        {
            return Math.Abs(val - value) < 0.001;
        }
        return false;
    }

    private static bool HasKeyAndBoolValue(Dictionary<string, string> dict, string key, bool value)
    {
        if (dict.TryGetValue(key, out var valStr) && bool.TryParse(valStr, out var val))
        {
            return val == value;
        }
        return false;
    }
}

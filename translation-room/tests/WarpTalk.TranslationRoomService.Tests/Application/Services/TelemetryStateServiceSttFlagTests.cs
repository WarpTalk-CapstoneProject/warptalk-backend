using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Enums;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-429. <c>is_stt_degraded</c> is read by AudioRoutePriorityResolver and, until now, written by
/// nothing — so the one degradation the resolver exists to weigh could never be set. The event
/// that should have set it, <c>stt_unavailable</c>, was not in the enum at all and was being
/// dead-lettered 381 times.
/// </summary>
public class TelemetryStateServiceSttFlagTests
{
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly TelemetryStateService _service;
    private readonly Guid _roomId = Guid.NewGuid();
    private readonly List<Dictionary<string, string>> _writes = new();

    public TelemetryStateServiceSttFlagTests()
    {
        _redis
            .Setup(r => r.HashSetAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<string, Dictionary<string, string>>((_, updates) => _writes.Add(updates))
            .Returns(Task.CompletedTask);
        _redis
            .Setup(r => r.KeyExpireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(Task.FromResult(true));
        _redis
            .Setup(r => r.GetHashAllAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, string>());

        _service = new TelemetryStateService(_redis.Object);
    }

    [Theory]
    [InlineData(AudioRoutingEventType.stt_unavailable)]
    [InlineData(AudioRoutingEventType.stt_recovered)]
    public void SttSignalsTravelTheTelemetryPath(AudioRoutingEventType eventType)
    {
        // tts_unavailable already did; its exact sibling did not, which is why it never reached
        // a handler at all.
        _service.IsTelemetryOrTransportEvent(eventType).Should().BeTrue();
    }

    [Fact]
    public async Task SttUnavailable_RaisesTheDegradedFlagTheResolverReads()
    {
        await _service.UpdateTransportFlagsAndResolvePayloadAsync(
            _roomId, AudioRoutingEventType.stt_unavailable);

        _writes.Should().ContainSingle()
            .Which.Should().Contain(new KeyValuePair<string, string>("is_stt_degraded", "true"));
    }

    [Fact]
    public async Task SttRecovered_ClearsIt()
    {
        // Without this the flag would latch: a single STT blip would leave the room degraded for
        // the rest of its life, which is a worse bug than the dead-lettering being fixed.
        await _service.UpdateTransportFlagsAndResolvePayloadAsync(
            _roomId, AudioRoutingEventType.stt_recovered);

        _writes.Should().ContainSingle()
            .Which.Should().Contain(new KeyValuePair<string, string>("is_stt_degraded", "false"));
    }

    [Fact]
    public async Task TheSttFlagDoesNotDisturbVoiceCloneOrDeliveryMode()
    {
        // Each branch of this mapping owns one key. An STT signal that also rewrote delivery_mode
        // would silently drop every room into text-only.
        await _service.UpdateTransportFlagsAndResolvePayloadAsync(
            _roomId, AudioRoutingEventType.stt_unavailable);

        _writes[0].Should().NotContainKey("delivery_mode");
        _writes[0].Should().NotContainKey("voice_clone_status");
    }
}

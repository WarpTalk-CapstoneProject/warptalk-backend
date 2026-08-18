using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// Flash mode is a ROOM setting, so the host owns it.
///
/// It sits in the same meeting panel as voice-clone consent and the dub-voice refresh, which are
/// both self-service, and that similarity is the trap: those change how ONE person is heard,
/// this changes how EVERYBODY in the room is transcribed. A participant flipping it would be
/// reconfiguring the pipeline underneath five other people who never asked.
/// </summary>
public class RoomFlashModeTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid GuestId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly RoomFlashModeService _service;

    public RoomFlashModeTests()
    {
        _service = new RoomFlashModeService(
            _rooms.Object,
            _participants.Object,
            _redis.Object,
            NullLogger<RoomFlashModeService>.Instance);
    }

    private void RoomIsHostedBy(Guid hostId) =>
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = RoomId, HostId = hostId });

    private void IsAParticipant(Guid userId) =>
        _participants.Setup(p => p.GetByRoomAndUserAsync(RoomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoomParticipant { Id = Guid.NewGuid(), UserId = userId });

    private static string Key => $"translationRoom:{RoomId}:flash_mode";

    // ── who may change it ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHostCanTurnFlashModeOn()
    {
        RoomIsHostedBy(HostId);

        var result = await _service.SetAsync(RoomId, HostId, enabled: true);

        Assert.True(result.IsSuccess);
        _redis.Verify(r => r.StringSetAsync(Key, "on", It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task AParticipantWhoIsNotTheHostCannotChangeItForEverybodyElse()
    {
        RoomIsHostedBy(HostId);

        var result = await _service.SetAsync(RoomId, GuestId, enabled: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
        _redis.Verify(
            r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()),
            Times.Never);
    }

    [Fact]
    public async Task TurningItOffWritesOffRatherThanDeletingTheKey()
    {
        // Deleting would be indistinguishable from "never set", which means the deployment
        // default — so a host turning it off in a fleet defaulted to on would not turn it off.
        RoomIsHostedBy(HostId);

        await _service.SetAsync(RoomId, HostId, enabled: false);

        _redis.Verify(r => r.StringSetAsync(Key, "off", It.IsAny<TimeSpan?>()), Times.Once);
        _redis.Verify(r => r.KeyDeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TheKeyIsGivenALifetimeRatherThanLivingForever()
    {
        // Redis here runs allkeys-lru and has evicted live meeting state before. Nothing this
        // service writes may be immortal.
        RoomIsHostedBy(HostId);

        await _service.SetAsync(RoomId, HostId, enabled: true);

        _redis.Verify(
            r => r.StringSetAsync(Key, "on", It.Is<TimeSpan?>(t => t.HasValue && t.Value > TimeSpan.Zero)),
            Times.Once);
    }

    [Fact]
    public async Task AFailedWriteIsReportedRatherThanReportedAsSuccess()
    {
        // The person just moved a switch and is about to listen for the difference. Saying it
        // worked makes the FEATURE look broken instead of the write.
        RoomIsHostedBy(HostId);
        _redis.Setup(r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.SetAsync(RoomId, HostId, enabled: true);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AMissingRoomIsNotFoundRatherThanForbidden()
    {
        _rooms.Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _service.SetAsync(RoomId, HostId, enabled: true);

        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    // ── who may read it ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnyParticipantCanSeeWhatTheHostChose()
    {
        // A guest's UI has to render the switch in the right position, not guess at it.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync("on");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task SomebodyWhoIsNotInTheRoomIsNotTold()
    {
        var result = await _service.GetAsync(RoomId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ARoomThatNeverSetItReadsAsOff()
    {
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync((string?)null);

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Fact]
    public async Task RedisBeingDownAnswersOffRatherThanFailingThePage()
    {
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Fact]
    public async Task TheKeyMatchesWhatTheIngressWorkerReads()
    {
        // The contract with livekit_ingress_worker._flash_mode_enabled. Both halves read and
        // write this exact string, and nothing else connects them — a rename on either side is
        // a switch that silently stops working.
        RoomIsHostedBy(HostId);

        await _service.SetAsync(RoomId, HostId, enabled: true);

        _redis.Verify(
            r => r.StringSetAsync($"translationRoom:{RoomId}:flash_mode", "on", It.IsAny<TimeSpan?>()),
            Times.Once);
    }
}

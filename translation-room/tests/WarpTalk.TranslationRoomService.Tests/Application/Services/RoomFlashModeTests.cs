using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
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

    /// <summary>What livekit_ingress_worker publishes its own default to, every heartbeat.</summary>
    private const string DefaultKey = "warptalk:stt:flash_mode_default";

    private void DeploymentDefaultIs(string? value) =>
        _redis.Setup(r => r.StringGetAsync(DefaultKey)).ReturnsAsync(value);

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
        // Set the opposite way, to prove the override is what is being read and not this.
        DeploymentDefaultIs("off");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Room, result.Value.Source);
    }

    [Fact]
    public async Task SomebodyWhoIsNotInTheRoomIsNotTold()
    {
        var result = await _service.GetAsync(RoomId, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task ARoomThatNeverSetItFollowsTheDeploymentRatherThanReadingAsOff()
    {
        // THE DEFECT THIS REPLACES. Reporting "off" for an untouched room was true of the
        // override and false of the room, and harmless only while the deployment also defaulted
        // to off. The day it defaulted to on, every host saw a switch saying "off" while their
        // room was streaming — and flipping it on and back off wrote a real override, taking
        // away the latency the display was wrong about.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync((string?)null);
        DeploymentDefaultIs("on");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Deployment, result.Value.Source);
    }

    [Fact]
    public async Task AnOffDeploymentIsReportedAsOffRatherThanAsUnknown()
    {
        // Absent and "off" are different answers. A deployment that publishes "off" HAS been
        // read, and the UI may say so plainly instead of hedging.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync((string?)null);
        DeploymentDefaultIs("off");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.False(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Deployment, result.Value.Source);
    }

    [Fact]
    public async Task NoOverrideAndNoPublishedDefaultIsUnknownRatherThanOff()
    {
        // No worker has published recently — a deploy, or an eviction on this allkeys-lru Redis.
        // False is what gets rendered because something must be, but it is not a reading, and
        // saying "unknown" is what stops the UI asserting a state nobody observed.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Unknown, result.Value.Source);
    }

    [Fact]
    public async Task TheSpellingsTheIngressWorkerAcceptsAreAcceptedHereToo()
    {
        // Both halves read the same keys, so a value set by hand with redis-cli must not mean
        // one thing to the pipeline and another to the switch describing it.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync("TRUE");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Room, result.Value.Source);
    }

    [Fact]
    public async Task AValueNeitherSideRecognisesIsTreatedAsUnsetRatherThanAsOff()
    {
        // Guessing at a typo is how a room ends up configured as nobody intended. An
        // unrecognised override falls through to the deployment, which is a value that was
        // actually written by something that knows what it means.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync("mostly");
        DeploymentDefaultIs("on");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Deployment, result.Value.Source);
    }

    [Fact]
    public async Task RedisBeingDownAnswersUnknownRatherThanFailingThePage()
    {
        // Still never an error: a switch that cannot be read must not take the meeting panel
        // with it. But "off" is a claim about the room, and this code just failed to make one.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.Equal(FlashModeSources.Unknown, result.Value!.Source);
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

    [Fact]
    public async Task TheDefaultKeyMatchesWhatTheIngressWorkerPublishes()
    {
        // The second half of the same contract. livekit_ingress_worker writes this exact string
        // on every heartbeat; nothing else connects the two, so a rename on either side puts
        // every untouched room back to reporting "unknown" with no error anywhere.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(Key)).ReturnsAsync((string?)null);
        _redis.Setup(r => r.StringGetAsync("warptalk:stt:flash_mode_default")).ReturnsAsync("on");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.Value!.Enabled);
        Assert.Equal(FlashModeSources.Deployment, result.Value.Source);
    }
}

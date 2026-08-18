using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// Denoising is a property of a MICROPHONE, so the person holding it owns the setting.
///
/// RoomFlashModeTests next door pins the opposite rule for the switch that sits beside this one in
/// the same panel, and the difference is the whole design: flash mode changes how everybody in the
/// room is transcribed, this changes how one caller's own microphone is handled and touches nobody
/// else's audio. Gating it on the host would mean a guest in a noisy room has to ask permission to
/// be understood — which is the failure this feature exists to remove.
///
/// It also pins the two ways the write half of WT-427 could have been born dead all over again:
/// the Redis key the AI side actually reads, and the lower-casing that key depends on.
/// </summary>
public class MicrophoneNoiseReductionTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();
    private static readonly Guid GuestId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<IRedisStateRepository> _redis = new();
    private readonly MicrophoneNoiseReductionService _service;

    public MicrophoneNoiseReductionTests()
    {
        _service = new MicrophoneNoiseReductionService(
            _participants.Object,
            _redis.Object,
            NullLogger<MicrophoneNoiseReductionService>.Instance);
    }

    private void IsAParticipant(Guid userId) =>
        _participants.Setup(p => p.GetByRoomAndUserAsync(RoomId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoomParticipant { Id = Guid.NewGuid(), UserId = userId });

    private static string KeyFor(Guid userId) =>
        $"translationRoom:{RoomId}:participant:{userId.ToString().ToLowerInvariant()}:noise_reduction";

    // ── who may change it ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGuestCanDenoiseTheirOwnMicrophoneWithoutAskingTheHost()
    {
        IsAParticipant(GuestId);

        var result = await _service.SetAsync(RoomId, GuestId, "far_field");

        Assert.True(result.IsSuccess);
        Assert.Equal("far_field", result.Value);
        _redis.Verify(
            r => r.StringSetAsync(KeyFor(GuestId), "far_field", It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task TheHostGetsNoSpecialTreatmentBecauseThereIsNothingToArbitrate()
    {
        IsAParticipant(HostId);

        var result = await _service.SetAsync(RoomId, HostId, "near_field");

        Assert.True(result.IsSuccess);
        _redis.Verify(
            r => r.StringSetAsync(KeyFor(HostId), "near_field", It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task SomebodyWhoIsNotInTheMeetingCannotWriteIntoIt()
    {
        // No IsAParticipant for StrangerId — the repository returns null.
        var result = await _service.SetAsync(RoomId, StrangerId, "far_field");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
        _redis.Verify(
            r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()),
            Times.Never);
    }

    [Fact]
    public async Task OneParticipantsChoiceIsWrittenUnderTheirOwnIdAndNobodyElses()
    {
        IsAParticipant(GuestId);

        await _service.SetAsync(RoomId, GuestId, "far_field");

        _redis.Verify(
            r => r.StringSetAsync(KeyFor(HostId), It.IsAny<string>(), It.IsAny<TimeSpan?>()),
            Times.Never);
    }

    // ── the key the AI side actually reads ───────────────────────────────────────────────────

    [Fact]
    public async Task TheKeyIsLowerCasedBecauseRedisKeysAreCaseSensitive()
    {
        // The AI side receives the speaker id as LiveKit reported it, and base_worker compares
        // SourceUserId with .lower() on BOTH sides because the two casings do not reliably agree.
        // An upper-cased id here would be a write half that never meets its reader.
        IsAParticipant(GuestId);

        await _service.SetAsync(RoomId, GuestId, "far_field");

        _redis.Verify(
            r => r.StringSetAsync(
                It.Is<string>(k => k.Contains(GuestId.ToString().ToLowerInvariant())
                                   && !k.Contains(GuestId.ToString().ToUpperInvariant())),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    [Fact]
    public async Task TheKeyIsTheOneSttWorkerReads()
    {
        // Spelled out rather than composed, so a rename on either side fails here instead of in a
        // meeting. This exact string is what STTWorker._get_noise_reduction builds.
        IsAParticipant(GuestId);

        await _service.SetAsync(RoomId, GuestId, "off");

        var expected =
            $"translationRoom:{RoomId}:participant:{GuestId.ToString().ToLowerInvariant()}"
            + ":noise_reduction";
        _redis.Verify(r => r.StringSetAsync(expected, "off", It.IsAny<TimeSpan?>()), Times.Once);
    }

    [Fact]
    public async Task NothingWrittenHereIsImmortal()
    {
        // Redis runs allkeys-lru here and has evicted live meeting state before; an immortal key
        // from an abandoned room is a contribution to that.
        IsAParticipant(GuestId);

        await _service.SetAsync(RoomId, GuestId, "far_field");

        _redis.Verify(
            r => r.StringSetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<TimeSpan?>(t => t.HasValue && t.Value > TimeSpan.Zero)),
            Times.Once);
    }

    // ── what a mode may be ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("off")]
    [InlineData("near_field")]
    [InlineData("far_field")]
    public async Task TheThreeModesTheProviderAcceptsAreAccepted(string mode)
    {
        IsAParticipant(GuestId);

        var result = await _service.SetAsync(RoomId, GuestId, mode);

        Assert.True(result.IsSuccess);
        Assert.Equal(mode, result.Value);
    }

    [Theory]
    [InlineData("aggressive")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("near-field")]
    public async Task AnythingElseIsRefusedHereRatherThanMidMeeting(string mode)
    {
        // An unrecognised string fails the WHOLE session update on the AI side, taking the language
        // hint and the keywords down with it — _degrade_session_config exists because that has
        // happened. So it is refused at the edge, before it can be written.
        IsAParticipant(GuestId);

        var result = await _service.SetAsync(RoomId, GuestId, mode);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _redis.Verify(
            r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()),
            Times.Never);
    }

    [Fact]
    public async Task ModeIsValidatedBeforeTheRoomIsEvenConsulted()
    {
        // Cheap and total: a nonsense mode can never be written for anyone, member or not.
        var result = await _service.SetAsync(RoomId, StrangerId, "aggressive");

        Assert.Equal(ErrorCodes.ValidationError, result.ErrorCode);
        _participants.Verify(
            p => p.GetByRoomAndUserAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CasingAndSurroundingSpaceAreNormalisedRatherThanRejected()
    {
        IsAParticipant(GuestId);

        var result = await _service.SetAsync(RoomId, GuestId, "  FAR_FIELD ");

        Assert.True(result.IsSuccess);
        Assert.Equal("far_field", result.Value);
        _redis.Verify(
            r => r.StringSetAsync(KeyFor(GuestId), "far_field", It.IsAny<TimeSpan?>()),
            Times.Once);
    }

    // ── reading it back ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AParticipantWhoHasNeverChosenReadsOffBecauseThatIsWhatTheAudioDoes()
    {
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(KeyFor(GuestId))).ReturnsAsync((string?)null);

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.Equal("off", result.Value);
    }

    [Fact]
    public async Task AStoredModeIsReadBack()
    {
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(KeyFor(GuestId))).ReturnsAsync("far_field");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.Equal("far_field", result.Value);
    }

    [Fact]
    public async Task AValueTheProviderWouldRejectReadsAsOffRatherThanAsItself()
    {
        // The AI side ignores what it does not recognise and falls back, so "off" is the honest
        // description of what the pipeline will do — not the stored string.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(KeyFor(GuestId))).ReturnsAsync("aggressive");

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.Equal("off", result.Value);
    }

    [Fact]
    public async Task ARedisOutageReadsAsOffRatherThanAsAnError()
    {
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringGetAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.GetAsync(RoomId, GuestId);

        Assert.True(result.IsSuccess);
        Assert.Equal("off", result.Value);
    }

    [Fact]
    public async Task AFailedWriteIsReportedRatherThanSwallowed()
    {
        // The person just changed a setting and is about to listen for the difference.
        IsAParticipant(GuestId);
        _redis.Setup(r => r.StringSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));

        var result = await _service.SetAsync(RoomId, GuestId, "far_field");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InternalServerError, result.ErrorCode);
    }

    [Fact]
    public async Task SomebodyNotInTheMeetingCannotReadOutOfItEither()
    {
        var result = await _service.GetAsync(RoomId, StrangerId);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotFound, result.ErrorCode);
    }
}

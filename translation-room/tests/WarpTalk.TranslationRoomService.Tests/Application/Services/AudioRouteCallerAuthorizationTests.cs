using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Enums;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-713. <c>POST audio-routes/generate</c>, <c>GET audio-routes</c> and
/// <c>PATCH audio-routes/{routeId}/runtime</c> took no user at all, so any signed-in account in
/// any tenant could read another meeting's route mesh, regenerate it, or rewrite a route's status.
/// The rule now: host for the two writes, host or participant for the read, and an outsider is
/// told the room does not exist (the WT-334 convention).
/// </summary>
public class AudioRouteCallerAuthorizationTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITranslationRoomRepository> _rooms = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participants = new();
    private readonly Mock<ITranslationRoomAudioRouteRepository> _routes = new();
    private readonly Mock<IAudioRouteCacheService> _cache = new();
    private readonly TranslationRoomAudioRouteService _service;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostUserId = Guid.NewGuid();
    private static readonly Guid ParticipantUserId = Guid.NewGuid();
    private static readonly Guid StrangerUserId = Guid.NewGuid();

    private readonly TranslationRoomAudioRoute _route = new()
    {
        Id = Guid.NewGuid(),
        TranslationRoomId = RoomId,
        SourceParticipantId = Guid.NewGuid(),
        TargetParticipantId = Guid.NewGuid(),
        SourceLanguage = "vi",
        TargetLanguage = "en",
        Status = AudioRouteStatus.PENDING.ToString(),
    };

    public AudioRouteCallerAuthorizationTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_rooms.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participants.Object);
        _unitOfWork.Setup(u => u.TranslationRoomAudioRouteRepository).Returns(_routes.Object);

        _rooms
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = RoomId, HostId = HostUserId, Title = "t", TranslationRoomCode = "ABCD" });
        _participants
            .Setup(p => p.GetByRoomAndUserAsync(RoomId, ParticipantUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoomParticipant { Id = Guid.NewGuid(), UserId = ParticipantUserId, TranslationRoomId = RoomId });
        _routes
            .Setup(r => r.GetRoutesByRoomIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TranslationRoomAudioRoute> { _route });
        _routes
            .Setup(r => r.GetByIdAsync(_route.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_route);

        _service = new TranslationRoomAudioRouteService(
            _unitOfWork.Object,
            _cache.Object,
            Mock.Of<IAudioRouteEventProcessor>(),
            Mock.Of<ILanguagePolicy>(),
            Mock.Of<IVoiceConsentDirectory>(),
            Mock.Of<IUserSettingsDirectory>(),
            Mock.Of<IRedisStateRepository>(),
            Mock.Of<ILogger<TranslationRoomAudioRouteService>>());
    }

    private static UpdateAudioRouteRuntimeContextDto Broadcasting() =>
        new("stream-1", AudioRouteStatus.BROADCASTING.ToString());

    private void VerifyRouteUntouched()
    {
        _routes.Verify(r => r.Update(It.IsAny<TranslationRoomAudioRoute>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _route.Status.Should().Be(AudioRouteStatus.PENDING.ToString());
    }

    [Fact]
    public async Task GetRoutes_ShouldAnswerNotFound_ForAUserOutsideTheRoom()
    {
        var result = await _service.GetRoutesAsync(RoomId, StrangerUserId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.Error.Should().Be(TranslationRoomConstants.ErrorRoomNotFound);
        _routes.Verify(r => r.GetRoutesByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRoutes_ShouldAnswerNotFound_WhenTheRoomDoesNotExist()
    {
        var result = await _service.GetRoutesAsync(Guid.NewGuid(), HostUserId);

        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetRoutes_ShouldReturnTheMesh_ToTheHostAndToParticipants(bool asHost)
    {
        var result = await _service.GetRoutesAsync(RoomId, asHost ? HostUserId : ParticipantUserId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(r => r.Id == _route.Id);
    }

    [Fact]
    public async Task UpdateRuntimeContext_ShouldAnswerNotFound_AndWriteNothing_ForAUserOutsideTheRoom()
    {
        var result = await _service.UpdateRuntimeContextAsync(RoomId, _route.Id, StrangerUserId, Broadcasting());

        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.Error.Should().Be(TranslationRoomConstants.ErrorRoomNotFound);
        VerifyRouteUntouched();
    }

    [Fact]
    public async Task UpdateRuntimeContext_ShouldAnswerForbidden_AndWriteNothing_ForANonHostParticipant()
    {
        var result = await _service.UpdateRuntimeContextAsync(RoomId, _route.Id, ParticipantUserId, Broadcasting());

        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        result.Error.Should().Be(AudioRouteConstants.ErrorHostOnlyRouteAction);
        VerifyRouteUntouched();
    }

    [Fact]
    public async Task UpdateRuntimeContext_ShouldApplyAndPublish_ForTheHost()
    {
        var result = await _service.UpdateRuntimeContextAsync(RoomId, _route.Id, HostUserId, Broadcasting());

        result.IsSuccess.Should().BeTrue();
        _route.Status.Should().Be(AudioRouteStatus.BROADCASTING.ToString());
        _cache.Verify(c => c.PublishRoutesUpdateAsync(RoomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateRuntimeContext_ShouldFollowAHostTransfer()
    {
        // IsHostedBy, not HostId: after a transfer the transferee holds the host's rights.
        var transferee = Guid.NewGuid();
        _rooms
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom
            {
                Id = RoomId, HostId = HostUserId, ActiveHostId = transferee, Title = "t", TranslationRoomCode = "ABCD",
            });

        var result = await _service.UpdateRuntimeContextAsync(RoomId, _route.Id, transferee, Broadcasting());

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateRoutesForCaller_ShouldRefuse_AnyoneButTheHost_BeforeTouchingTheMesh(bool isParticipant)
    {
        var caller = isParticipant ? ParticipantUserId : StrangerUserId;

        var result = await _service.GenerateRoutesForCallerAsync(RoomId, caller);

        result.ErrorCode.Should().Be(isParticipant ? ErrorCodes.Forbidden : ErrorCodes.NotFound);
        _participants.Verify(p => p.GetByRoomIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.PublishRoutesUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateRoutesForCaller_ShouldReachGeneration_ForTheHost()
    {
        // The room in this fixture has no language policy, so generation itself stops at its own
        // guard — which is the point: the host got PAST authorization and into GenerateRoutesAsync.
        var result = await _service.GenerateRoutesForCallerAsync(RoomId, HostUserId);

        result.Error.Should().Be(AudioRouteConstants.ErrorRoomPolicyIncomplete);
    }
}

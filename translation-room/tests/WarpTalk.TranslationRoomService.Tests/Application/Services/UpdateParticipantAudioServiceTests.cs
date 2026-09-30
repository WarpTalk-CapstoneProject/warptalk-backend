using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Constants;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// TranslationRoomParticipantService.UpdateParticipantAudioAsync — the host turning a participant's
/// translated-audio relay on or off. The non-host (Forbidden) case lives in
/// ParticipantManagementServiceTests.UpdateParticipantAudioAsync_ShouldReturnForbidden_WhenRequesterIsNotHost.
/// </summary>
public class UpdateParticipantAudioServiceTests
{
    private readonly Mock<ITranslationRoomRepository> _roomRepositoryMock = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepositoryMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly Mock<ILogger<TranslationRoomParticipantService>> _loggerMock = new();
    private readonly TranslationRoomParticipantService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    public UpdateParticipantAudioServiceTests()
    {
        _unitOfWorkMock.Setup(uow => uow.TranslationRoomRepository).Returns(_roomRepositoryMock.Object);
        _unitOfWorkMock.Setup(uow => uow.TranslationRoomParticipantRepository).Returns(_participantRepositoryMock.Object);
        _unitOfWorkMock.Setup(uow => uow.TranslationRoomInvitationRepository).Returns(Mock.Of<ITranslationRoomInvitationRepository>());

        _sut = new TranslationRoomParticipantService(
            _unitOfWorkMock.Object,
            Mock.Of<IWorkspaceMemberDirectory>(),
            _loggerMock.Object);
    }

    private void RoomExists() =>
        _roomRepositoryMock
            .Setup(repo => repo.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranslationRoom { Id = RoomId, HostId = HostId });

    private TranslationRoomParticipant ParticipantExists(Guid roomId, bool isTranslationAudioEnabled)
    {
        var participant = new TranslationRoomParticipant
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = roomId,
            Status = "CONNECTED",
            IsTranslationAudioEnabled = isTranslationAudioEnabled,
        };
        _participantRepositoryMock
            .Setup(repo => repo.GetByIdAsync(participant.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        return participant;
    }

    private void VerifyNothingSaved()
    {
        _participantRepositoryMock.Verify(repo => repo.Update(It.IsAny<TranslationRoomParticipant>()), Times.Never);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // UTCID01
    public async Task UpdateParticipantAudioAsync_ShouldDisableTranslationAudio_WhenHostTurnsItOff()
    {
        RoomExists();
        var participant = ParticipantExists(RoomId, isTranslationAudioEnabled: true);

        var result = await _sut.UpdateParticipantAudioAsync(RoomId, participant.Id, new UpdateParticipantAudioRequest(false), HostId);

        result.IsSuccess.Should().BeTrue();
        participant.IsTranslationAudioEnabled.Should().BeFalse();
        _participantRepositoryMock.Verify(repo => repo.Update(
            It.Is<TranslationRoomParticipant>(p => p.Id == participant.Id && !p.IsTranslationAudioEnabled)), Times.Once);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // UTCID02
    public async Task UpdateParticipantAudioAsync_ShouldReturnNotFound_WhenRoomDoesNotExist()
    {
        _roomRepositoryMock
            .Setup(repo => repo.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _sut.UpdateParticipantAudioAsync(RoomId, Guid.NewGuid(), new UpdateParticipantAudioRequest(false), HostId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TranslationRoomConstants.ErrorRoomNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        VerifyNothingSaved();
    }

    [Fact] // UTCID04
    public async Task UpdateParticipantAudioAsync_ShouldReturnNotFound_WhenParticipantDoesNotExist()
    {
        RoomExists();
        var participantId = Guid.NewGuid();
        _participantRepositoryMock
            .Setup(repo => repo.GetByIdAsync(participantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoomParticipant?)null);

        var result = await _sut.UpdateParticipantAudioAsync(RoomId, participantId, new UpdateParticipantAudioRequest(false), HostId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TranslationRoomConstants.ErrorParticipantNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        VerifyNothingSaved();
    }

    [Fact] // UTCID05
    public async Task UpdateParticipantAudioAsync_ShouldEnableTranslationAudio_WhenHostTurnsItOn()
    {
        RoomExists();
        var participant = ParticipantExists(RoomId, isTranslationAudioEnabled: false);

        var result = await _sut.UpdateParticipantAudioAsync(RoomId, participant.Id, new UpdateParticipantAudioRequest(true), HostId);

        result.IsSuccess.Should().BeTrue();
        participant.IsTranslationAudioEnabled.Should().BeTrue();
        _participantRepositoryMock.Verify(repo => repo.Update(
            It.Is<TranslationRoomParticipant>(p => p.Id == participant.Id && p.IsTranslationAudioEnabled)), Times.Once);
        _unitOfWorkMock.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact] // UTCID06
    public async Task UpdateParticipantAudioAsync_ShouldReturnNotFound_WhenParticipantBelongsToAnotherRoom()
    {
        RoomExists();
        var participant = ParticipantExists(Guid.NewGuid(), isTranslationAudioEnabled: true);

        var result = await _sut.UpdateParticipantAudioAsync(RoomId, participant.Id, new UpdateParticipantAudioRequest(false), HostId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TranslationRoomConstants.ErrorParticipantNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        participant.IsTranslationAudioEnabled.Should().BeTrue();
        VerifyNothingSaved();
    }

    [Fact] // UTCID07
    public async Task UpdateParticipantAudioAsync_ShouldReturnInternalServerErrorAndLog_WhenSaveChangesThrows()
    {
        RoomExists();
        var participant = ParticipantExists(RoomId, isTranslationAudioEnabled: true);
        _unitOfWorkMock
            .Setup(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await _sut.UpdateParticipantAudioAsync(RoomId, participant.Id, new UpdateParticipantAudioRequest(false), HostId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(TranslationRoomConstants.ErrorUnexpectedUpdateParticipantAudio);
        result.ErrorCode.Should().Be(ErrorCodes.InternalServerError);
        _loggerMock.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString() ==
                $"Error occurred while updating participant audio. RoomId: {RoomId}, ParticipantId: {participant.Id}"),
            It.IsAny<InvalidOperationException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-704: the mesh room read carries the generatable artifact languages only when the caller
/// opts in. The plain read sits on hot paths across the mesh and must not pay for the workspace
/// RPC and catalog read behind the answer.
/// </summary>
public class TranslationRoomDirectoryServiceArtifactLanguagesTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRoomArtifactLanguagePolicy> _policy = new();
    private readonly TranslationRoom _room;

    public TranslationRoomDirectoryServiceArtifactLanguagesTests()
    {
        _room = new TranslationRoom
        {
            Id = RoomId,
            HostId = HostId,
            WorkspaceId = Guid.NewGuid(),
            Title = "Quarterly planning",
            // Deliberately NOT terminal: unlike the user-facing detail read, an opted-in mesh
            // caller gets an answer whatever state the room is in.
            Status = "IN_PROGRESS",
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\",\"ja\"]",
            IsActive = true
        };

        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_room);
    }

    private TranslationRoomDirectoryService CreateSut(IRoomArtifactLanguagePolicy? policy) =>
        new(_roomRepository.Object, _participantRepository.Object, _unitOfWork.Object, policy);

    [Fact]
    public async Task FlagOff_DoesNotConsultThePolicy()
    {
        var result = await CreateSut(_policy.Object).GetRoomAsync(RoomId, includeArtifactLanguages: false);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().BeNull();
        _policy.Verify(
            p => p.GetGeneratableLanguagesAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TwoArgumentOverload_IsTheUnchangedMeshRead()
    {
        var result = await CreateSut(_policy.Object).GetRoomAsync(RoomId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().BeNull();
        _policy.Verify(
            p => p.GetGeneratableLanguagesAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FlagOn_CarriesThePolicyAnswer_ForAnyStatus()
    {
        _policy
            .Setup(p => p.GetGeneratableLanguagesAsync(_room, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "vi", "en" });

        var result = await CreateSut(_policy.Object).GetRoomAsync(RoomId, includeArtifactLanguages: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().NotBeNull();
        result.Value!.ArtifactLanguages!.Generatable.Should().Equal("vi", "en");
    }

    [Fact]
    public async Task FlagOn_PolicyThrows_LeavesTheListUnresolved_AndTheReadSucceeds()
    {
        _policy
            .Setup(p => p.GetGeneratableLanguagesAsync(_room, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("workspace down"));

        var result = await CreateSut(_policy.Object).GetRoomAsync(RoomId, includeArtifactLanguages: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().BeNull();
    }

    [Fact]
    public async Task FlagOn_Cancellation_IsNotSwallowed()
    {
        _policy
            .Setup(p => p.GetGeneratableLanguagesAsync(_room, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => CreateSut(_policy.Object).GetRoomAsync(RoomId, includeArtifactLanguages: true);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FlagOn_WithoutAPolicy_LeavesTheListUnresolved()
    {
        var result = await CreateSut(policy: null).GetRoomAsync(RoomId, includeArtifactLanguages: true);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().BeNull();
    }
}

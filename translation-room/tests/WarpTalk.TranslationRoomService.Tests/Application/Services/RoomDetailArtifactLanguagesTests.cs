using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.LanguagePolicy;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-703. The room detail is where the web learns which languages it may offer for generating a
/// finished meeting's summary or minutes — it cannot work that out itself, because a guest gets 403
/// on the workspace settings the answer depends on.
///
/// Pins both halves: a finished room carries exactly what <see cref="IRoomArtifactLanguagePolicy"/>
/// says, and a live or upcoming room carries nothing and never triggers the lookup.
/// </summary>
public class RoomDetailArtifactLanguagesTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();
    private readonly Mock<IRoomArtifactLanguagePolicy> _artifactLanguagePolicy = new(MockBehavior.Strict);

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    public RoomDetailArtifactLanguagesTests()
    {
        _unitOfWork.Setup(u => u.TranslationRoomRepository).Returns(_roomRepository.Object);
        _unitOfWork.Setup(u => u.TranslationRoomParticipantRepository).Returns(_participantRepository.Object);
    }

    [Theory]
    [InlineData("ENDED")]
    [InlineData("CANCELLED")]
    [InlineData("EXPIRED")]
    public async Task ReadDetail_FinishedRoom_CarriesThePolicysGeneratableLanguages(string status)
    {
        var room = GivenRoom(status);
        _artifactLanguagePolicy
            .Setup(p => p.GetGeneratableLanguagesAsync(room, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "vi", "en" });

        var result = await CreateSut(_artifactLanguagePolicy.Object).GetTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().NotBeNull();
        result.Value.ArtifactLanguages!.Generatable.Should().Equal("vi", "en");
    }

    [Theory]
    [InlineData("SCHEDULED")]
    [InlineData("WAITING")]
    [InlineData("IN_PROGRESS")]
    [InlineData("PAUSED")]
    public async Task ReadDetail_UnfinishedRoom_HasNoArtifactLanguages_AndNeverAsksThePolicy(string status)
    {
        GivenRoom(status);

        var result = await CreateSut(_artifactLanguagePolicy.Object).GetTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().BeNull();
        _artifactLanguagePolicy.Verify(
            p => p.GetGeneratableLanguagesAsync(It.IsAny<TranslationRoom>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReadDetail_WithoutAPolicy_HasNoArtifactLanguages()
    {
        GivenRoom("ENDED");

        var result = await CreateSut(artifactLanguagePolicy: null).GetTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ArtifactLanguages.Should().BeNull();
    }

    /// <summary>
    /// The detail page is on the join path; a policy that throws must hide the language choice, not
    /// take the whole room read down with it.
    /// </summary>
    [Fact]
    public async Task ReadDetail_PolicyThrows_StillReturnsTheRoom_WithoutArtifactLanguages()
    {
        var room = GivenRoom("ENDED");
        _artifactLanguagePolicy
            .Setup(p => p.GetGeneratableLanguagesAsync(room, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await CreateSut(_artifactLanguagePolicy.Object).GetTranslationRoomAsync(RoomId, HostId, null);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be(RoomId);
        result.Value.ArtifactLanguages.Should().BeNull();
    }

    private WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService CreateSut(
        IRoomArtifactLanguagePolicy? artifactLanguagePolicy) =>
        new(
            _unitOfWork.Object,
            Mock.Of<ILanguagePolicy>(),
            Mock.Of<IAudioRouteEventProcessor>(),
            Mock.Of<ITranslationRoomAudioRouteService>(),
            Mock.Of<IUserSettingsDirectory>(),
            Mock.Of<IWorkspaceMeetingPolicy>(),
            Mock.Of<IWorkspaceMemberDirectory>(),
            Mock.Of<WarpTalk.Shared.Interfaces.IEmailService>(),
            Mock.Of<ILogger<WarpTalk.TranslationRoomService.Application.Services.TranslationRoomService>>(),
            artifactLanguagePolicy: artifactLanguagePolicy);

    private TranslationRoom GivenRoom(string status)
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            HostId = HostId,
            WorkspaceId = Guid.NewGuid(),
            Title = "Quarterly planning",
            Status = status,
            SourceLanguage = "vi",
            TargetLanguages = "[\"en\"]",
            IsActive = true,
            DeletedAt = null,
            TranslationRoomParticipants = new List<TranslationRoomParticipant>(),
            TranslationRoomInvitations = new List<TranslationRoomInvitation>()
        };

        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
        _roomRepository.Setup(r => r.Query()).Returns(new[] { room }.AsQueryable());
        return room;
    }
}

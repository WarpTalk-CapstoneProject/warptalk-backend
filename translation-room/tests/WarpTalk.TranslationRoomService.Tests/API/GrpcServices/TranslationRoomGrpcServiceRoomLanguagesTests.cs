using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Moq;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranslationRoomService.API.GrpcServices;
using WarpTalk.TranslationRoomService.Application.DTOs;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Enums;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.API.GrpcServices;

/// <summary>
/// WT-704: <c>GetTranslationRoomById</c> always carries the room's declared languages, and sets
/// <c>artifact_languages_resolved</c> only alongside a real answer — so a computation failure can
/// never read as "nothing may be generated".
/// </summary>
public class TranslationRoomGrpcServiceRoomLanguagesTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    private readonly Mock<ITranslationRoomDirectoryService> _directory = new();
    private readonly TranslationRoomGrpcService _sut;

    public TranslationRoomGrpcServiceRoomLanguagesTests()
    {
        _sut = new TranslationRoomGrpcService(_directory.Object);
    }

    [Fact]
    public async Task NotRequested_FillsDeclaredLanguages_AndLeavesArtifactLanguagesUnresolved()
    {
        GivenRoom(includeArtifactLanguages: false, Dto(artifactLanguages: null));

        var response = await _sut.GetTranslationRoomById(
            new GetTranslationRoomRequest { Id = RoomId.ToString() }, new TestServerCallContext());

        response.SourceLanguage.Should().Be("vi");
        response.TargetLanguages.Should().Equal("en", "ja");
        response.GeneratableArtifactLanguages.Should().BeEmpty();
        response.ArtifactLanguagesResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Requested_AndResolved_CarriesTheList()
    {
        GivenRoom(includeArtifactLanguages: true, Dto(new RoomArtifactLanguagesDto(new List<string> { "vi", "en" })));

        var response = await _sut.GetTranslationRoomById(
            new GetTranslationRoomRequest { Id = RoomId.ToString(), IncludeArtifactLanguages = true },
            new TestServerCallContext());

        response.GeneratableArtifactLanguages.Should().Equal("vi", "en");
        response.ArtifactLanguagesResolved.Should().BeTrue();
    }

    [Fact]
    public async Task Requested_AndResolvedEmpty_IsAnAnswer()
    {
        GivenRoom(includeArtifactLanguages: true, Dto(new RoomArtifactLanguagesDto(new List<string>())));

        var response = await _sut.GetTranslationRoomById(
            new GetTranslationRoomRequest { Id = RoomId.ToString(), IncludeArtifactLanguages = true },
            new TestServerCallContext());

        response.GeneratableArtifactLanguages.Should().BeEmpty();
        response.ArtifactLanguagesResolved.Should().BeTrue();
    }

    [Fact]
    public async Task Requested_ButNotComputed_StaysUnresolved()
    {
        GivenRoom(includeArtifactLanguages: true, Dto(artifactLanguages: null));

        var response = await _sut.GetTranslationRoomById(
            new GetTranslationRoomRequest { Id = RoomId.ToString(), IncludeArtifactLanguages = true },
            new TestServerCallContext());

        response.ArtifactLanguagesResolved.Should().BeFalse();
        response.SourceLanguage.Should().Be("vi");
        response.TargetLanguages.Should().Equal("en", "ja");
    }

    private void GivenRoom(bool includeArtifactLanguages, TranslationRoomDto dto) =>
        _directory
            .Setup(d => d.GetRoomAsync(RoomId, includeArtifactLanguages, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

    private static TranslationRoomDto Dto(RoomArtifactLanguagesDto? artifactLanguages) =>
        new(
            Id: RoomId,
            WorkspaceId: Guid.NewGuid(),
            HostId: Guid.NewGuid(),
            Title: "Quarterly planning",
            Description: null,
            TranslationRoomCode: "ABCDEF",
            Status: RoomStatus.ENDED,
            TranslationRoomType: string.Empty,
            MaxParticipants: 10,
            SourceLanguage: "vi",
            TargetLanguages: new List<string> { "en", "ja" },
            ScheduledAt: null,
            InvitedEmails: null,
            StartedAt: null,
            EndedAt: null,
            DurationSeconds: null,
            CreatedAt: DateTime.UtcNow,
            Settings: new RoomSettingsResponse(true, "HOST_ONLY", false, false, false, false),
            ParticipantCount: 0,
            ArtifactLanguages: artifactLanguages);

    private sealed class TestServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "TestMethod";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => null!;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => null!;
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}

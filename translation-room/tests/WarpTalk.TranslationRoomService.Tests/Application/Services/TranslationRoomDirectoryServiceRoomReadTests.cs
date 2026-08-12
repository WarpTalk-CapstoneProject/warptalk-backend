using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using WarpTalk.Shared;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Application.Services;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranslationRoomService.Tests.Application.Services;

/// <summary>
/// WT-334. The other half of the fix: tightening the HTTP read must not break the mesh.
///
/// <c>TranslationRoomGrpcService.GetTranslationRoomById</c> is called service-to-service with no
/// user context — WorkspaceService's <c>DocumentAccessEvaluator</c> and
/// <c>WorkspaceDocumentService</c> resolve a room to authorize a DOCUMENT, and there is no end user
/// on that call to check against. So the unchecked read moved to
/// <see cref="ITranslationRoomDirectoryService"/> rather than being deleted or given a nullable
/// "skip the check" flag.
///
/// These tests exist to make that exemption deliberate and visible. If someone later "finishes the
/// job" by adding a user check here, the mesh breaks — and
/// <see cref="MeshRead_ShouldSucceed_WithNoUserContextAtAll"/> is what tells them that was the
/// point, not an oversight.
/// </summary>
public class TranslationRoomDirectoryServiceRoomReadTests
{
    private readonly Mock<ITranslationRoomRepository> _roomRepository = new();
    private readonly Mock<ITranslationRoomParticipantRepository> _participantRepository = new();

    /// <summary>
    /// WT-359 gave this service its first write (TransferHostAsync), so it now takes a unit of
    /// work. These are read tests and never reach it — it is here to satisfy the constructor.
    /// </summary>
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private readonly TranslationRoomDirectoryService _sut;

    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid HostId = Guid.NewGuid();

    public TranslationRoomDirectoryServiceRoomReadTests()
    {
        _participantRepository
            .Setup(p => p.CountSeatHoldingParticipantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        _sut = new TranslationRoomDirectoryService(
            _roomRepository.Object,
            _participantRepository.Object,
            _unitOfWork.Object);
    }

    /// <summary>
    /// The service-to-service path: no user id, no email, no claims — and it still resolves the
    /// room. This is the case that would have broken had WT-334 simply added a guard to the one
    /// shared method.
    /// </summary>
    [Fact]
    public async Task MeshRead_ShouldSucceed_WithNoUserContextAtAll()
    {
        GivenRoom();

        var result = await _sut.GetRoomAsync(RoomId);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be(RoomId);
        result.Value!.HostId.Should().Be(HostId);
    }

    /// <summary>
    /// The mesh read must NOT consult the room-read predicate — it has nobody to evaluate it
    /// against. Query() is how <c>RoomReadAccess.IsReadableBy</c> is applied, so never touching it
    /// is the observable form of "this path is deliberately unauthorized".
    /// </summary>
    [Fact]
    public async Task MeshRead_ShouldNotEvaluateTheRoomReadPredicate()
    {
        GivenRoom();

        await _sut.GetRoomAsync(RoomId);

        _roomRepository.Verify(r => r.Query(), Times.Never);
    }

    [Fact]
    public async Task MeshRead_ShouldReturnNotFound_WhenTheRoomDoesNotExist()
    {
        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TranslationRoom?)null);

        var result = await _sut.GetRoomAsync(RoomId);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>
    /// The exemption is a property of the TYPE, not of a parameter — that is the whole reason it
    /// was shaped this way instead of as a nullable userId. This pins it: the gRPC service must not
    /// depend on <see cref="ITranslationRoomService"/>, whose reads now require a caller. If a
    /// future RPC reaches back to the user-facing service, it would be reaching for a method that
    /// either refuses everything (no user) or gets handed a fake one.
    /// </summary>
    [Fact]
    public void GrpcService_ShouldNotDependOnTheUserFacingRoomService()
    {
        var constructorParameterTypes = typeof(WarpTalk.TranslationRoomService.API.GrpcServices.TranslationRoomGrpcService)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        constructorParameterTypes.Should().NotContain(typeof(ITranslationRoomService));
        constructorParameterTypes.Should().Contain(typeof(ITranslationRoomDirectoryService));
    }

    private void GivenRoom()
    {
        var room = new TranslationRoom
        {
            Id = RoomId,
            HostId = HostId,
            WorkspaceId = Guid.NewGuid(),
            Title = "Cross-service lookup",
            Status = "ENDED",
            IsActive = true
        };

        _roomRepository
            .Setup(r => r.GetByIdAsync(RoomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);
    }
}

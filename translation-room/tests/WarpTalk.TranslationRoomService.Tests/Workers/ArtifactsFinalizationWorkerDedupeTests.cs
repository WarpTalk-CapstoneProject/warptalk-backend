using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// k8s multi-replica dedupe. The finalization queue is in-process but its producers are spread
/// over every replica (End, the reconciliation sweep, a host's regenerate), and each finalization
/// writes a new transcript + summary artifact and rings "Summary ready" for everyone who attended.
/// A room is finalized by one replica at a time, and never again once its artifacts exist.
/// </summary>
public sealed class ArtifactsFinalizationWorkerDedupeTests
{
    private static readonly Guid RoomId = Guid.Parse("7a7a7a7a-0000-0000-0000-000000000001");

    private readonly Mock<IArtifactsFinalizer> _finalizer = new();
    private readonly Mock<ITranslationRoomArtifactRepository> _artifacts = new();
    private readonly IDistributedLockProvider _locks =
        new DistributedLockProvider(new InProcessLeaseStore(TimeProvider.System), TimeProvider.System);

    private ArtifactsFinalizationWorker NewReplica()
    {
        var rooms = new Mock<ITranslationRoomRepository>();
        rooms.Setup(r => r.FindAsync(It.IsAny<Expression<Func<TranslationRoom, bool>>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TranslationRoom>());
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.TranslationRoomArtifactRepository).Returns(_artifacts.Object);
        unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(rooms.Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped(_ => _finalizer.Object);

        return new ArtifactsFinalizationWorker(
            Mock.Of<IArtifactsFinalizationQueue>(),
            services.BuildServiceProvider(),
            new Mock<NotificationClient>().Object,
            Options.Create(new AppSettings { FrontendBaseUrl = "https://warptalk.test" }),
            NullLogger<ArtifactsFinalizationWorker>.Instance,
            _locks);
    }

    private void ArtifactsExist(bool exist) =>
        _artifacts.Setup(a => a.AnyAsync(It.IsAny<Expression<Func<TranslationRoomArtifact, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(exist);

    private void VerifyFinalized(Times times) =>
        _finalizer.Verify(
            f => f.ProcessRoomFinalizationAsync(RoomId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            times);

    [Fact]
    public async Task ARoomWithNoArtifacts_IsFinalized()
    {
        ArtifactsExist(false);

        var outcome = await NewReplica().FinalizeOnceAsync(new FinalizationRequest(RoomId), CancellationToken.None);

        outcome.Should().Be(ExclusiveTickOutcome.Ran);
        VerifyFinalized(Times.Once());
    }

    [Fact]
    public async Task WhileAnotherReplicaIsFinalizingTheRoom_ThisReplicaDropsTheRequest()
    {
        ArtifactsExist(false);
        await using var otherReplica = await _locks.TryAcquireAsync(
            ArtifactsFinalizationWorker.LockResourceFor(RoomId), TimeSpan.FromMinutes(2));
        otherReplica.Should().NotBeNull();

        var outcome = await NewReplica().FinalizeOnceAsync(new FinalizationRequest(RoomId), CancellationToken.None);

        outcome.Should().Be(ExclusiveTickOutcome.Skipped);
        VerifyFinalized(Times.Never());
    }

    [Fact]
    public async Task ConcurrentRequestsOnTwoReplicas_FinalizeTheRoomOnce()
    {
        ArtifactsExist(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _finalizer
            .Setup(f => f.ProcessRoomFinalizationAsync(RoomId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
            });

        var first = NewReplica().FinalizeOnceAsync(new FinalizationRequest(RoomId), CancellationToken.None);
        await started.Task;
        var second = await NewReplica().FinalizeOnceAsync(new FinalizationRequest(RoomId), CancellationToken.None);
        release.SetResult();

        (await first).Should().Be(ExclusiveTickOutcome.Ran);
        second.Should().Be(ExclusiveTickOutcome.Skipped);
        VerifyFinalized(Times.Once());
    }

    [Fact]
    public async Task ARoomThatAlreadyHasItsArtifacts_IsNotFinalizedAgain()
    {
        ArtifactsExist(true);

        var outcome = await NewReplica().FinalizeOnceAsync(new FinalizationRequest(RoomId), CancellationToken.None);

        outcome.Should().Be(ExclusiveTickOutcome.Ran);
        VerifyFinalized(Times.Never());
    }
}

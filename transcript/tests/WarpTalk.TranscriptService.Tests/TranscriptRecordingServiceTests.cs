using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Services;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using Xunit;

namespace WarpTalk.TranscriptService.Tests;

/// <summary>
/// WT-605. Pause/Resume Transcript is host-only and idempotent — a second Pause while already
/// paused, or a Resume while not paused, must fail rather than silently succeed, since either
/// would otherwise let a second TranscriptPauseWindow overlap the first or resume a window that
/// was never opened.
/// </summary>
public class TranscriptRecordingServiceTests
{
    private static readonly Guid RoomId = Guid.NewGuid();

    [Fact]
    public async Task NonHost_CannotPause()
    {
        var host = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (service, _) = CreateService(host, activeWindow: null);

        var result = await service.PauseAsync(RoomId, stranger);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task NonHost_CannotResume()
    {
        var host = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (service, _) = CreateService(host, activeWindow: new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = DateTime.UtcNow,
            PausedBy = host,
        });

        var result = await service.ResumeAsync(RoomId, stranger);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task Host_CanPause_WhenNotAlreadyPaused()
    {
        var host = Guid.NewGuid();
        var (service, windows) = CreateService(host, activeWindow: null);

        var result = await service.PauseAsync(RoomId, host);

        Assert.True(result.IsSuccess);
        await windows.Received(1).AddAsync(
            Arg.Is<TranscriptPauseWindow>(w => w.TranslationRoomId == RoomId && w.PausedBy == host && w.EndedAt == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Host_CannotPause_WhenAlreadyPaused()
    {
        var host = Guid.NewGuid();
        var (service, windows) = CreateService(host, activeWindow: new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = DateTime.UtcNow,
            PausedBy = host,
        });

        var result = await service.PauseAsync(RoomId, host);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_STATE", result.ErrorCode);
        await windows.DidNotReceive().AddAsync(Arg.Any<TranscriptPauseWindow>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Host_CannotResume_WhenNotPaused()
    {
        var host = Guid.NewGuid();
        var (service, windows) = CreateService(host, activeWindow: null);

        var result = await service.ResumeAsync(RoomId, host);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_STATE", result.ErrorCode);
        windows.DidNotReceive().Update(Arg.Any<TranscriptPauseWindow>());
    }

    [Fact]
    public async Task Host_CanResume_ClosingTheOpenWindow()
    {
        var host = Guid.NewGuid();
        var active = new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            PausedBy = host,
        };
        var (service, windows) = CreateService(host, activeWindow: active);

        var result = await service.ResumeAsync(RoomId, host);

        Assert.True(result.IsSuccess);
        Assert.NotNull(active.EndedAt);
        Assert.Equal(host, active.ResumedBy);
        windows.Received(1).Update(active);
    }

    /// <summary>A publish failure (no Redis configured) must not fail the write that already
    /// committed — same posture as TranslationRoomService.PublishTranslationStoppedAsync.</summary>
    [Fact]
    public async Task Pause_Succeeds_EvenWithNoRedisConfigured()
    {
        var host = Guid.NewGuid();
        var (service, _) = CreateService(host, activeWindow: null, redis: false);

        var result = await service.PauseAsync(RoomId, host);

        Assert.True(result.IsSuccess);
    }

    private static (TranscriptRecordingService Service, ITranscriptPauseWindowRepository Windows) CreateService(
        Guid host, TranscriptPauseWindow? activeWindow, bool redis = true)
    {
        var windows = Substitute.For<ITranscriptPauseWindowRepository>();
        windows.GetActiveWindowByRoomIdAsync(RoomId, Arg.Any<CancellationToken>()).Returns(activeWindow);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.TranscriptPauseWindows.Returns(windows);

        var pauseAccess = new TranscriptPauseAccess(new FakeRoomClient(host));
        var readAccess = Substitute.For<ITranscriptReadAccess>();

        var service = new TranscriptRecordingService(
            unitOfWork,
            pauseAccess,
            readAccess,
            NullLogger<TranscriptRecordingService>.Instance,
            redis ? Substitute.For<StackExchange.Redis.IConnectionMultiplexer>() : null);

        return (service, windows);
    }

    /// <summary>Stand-in for the generated gRPC client — same approach as TranscriptReadAccessTests.</summary>
    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        private readonly Guid _hostId;

        public FakeRoomClient(Guid hostId)
        {
            _hostId = hostId;
        }

        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            return Call(new GetTranslationRoomResponse
            {
                Id = request.Id,
                HostId = _hostId.ToString(),
                Title = "Room",
                Status = "IN_PROGRESS"
            });
        }

        private static AsyncUnaryCall<T> Call<T>(T value) => new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Application.Authorization;
using WarpTalk.TranscriptService.Application.Interfaces;
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
        var (service, _, _) = CreateService(host, activeWindow: null);

        var result = await service.PauseAsync(RoomId, stranger);

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.ErrorCode);
    }

    [Fact]
    public async Task NonHost_CannotResume()
    {
        var host = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (service, _, _) = CreateService(host, activeWindow: new TranscriptPauseWindow
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
        var (service, windows, _) = CreateService(host, activeWindow: null);

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
        var (service, windows, _) = CreateService(host, activeWindow: new TranscriptPauseWindow
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
        var (service, windows, _) = CreateService(host, activeWindow: null);

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
        var (service, windows, _) = CreateService(host, activeWindow: active);

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
        var (service, _, _) = CreateService(host, activeWindow: null, redis: false);

        var result = await service.PauseAsync(RoomId, host);

        Assert.True(result.IsSuccess);
    }

    // ── The durable pause flag (the cross-repo half of WT-605) ──

    /// <summary>
    /// Pause writes <see cref="TranscriptPauseKey"/>, with a TTL.
    /// </summary>
    /// <remarks>
    /// The pub/sub publish beside it is not enough and never was: pub/sub has no backlog, so a
    /// consumer that starts after the host pressed Pause is never told, and one restarted mid-pause
    /// comes back believing the room is recording. Both are ordinary — warptalk-ai's workers and
    /// the Gateway are separate deployments on their own restart schedules — and both fail in the
    /// direction that writes down words somebody asked not to be written down.
    ///
    /// The key name is asserted literally because it is a contract with two other repositories.
    /// Renaming it breaks nothing loudly; it turns every reader's gate off in silence.
    /// </remarks>
    [Fact]
    public async Task Pause_WritesTheDurableFlagWithATtl()
    {
        var host = Guid.NewGuid();
        var (service, _, database) = CreateService(host, activeWindow: null);

        Assert.True((await service.PauseAsync(RoomId, host)).IsSuccess);

        // Read off the received call rather than matched with Arg.Is per parameter: StringSetAsync
        // has six overloads and which one a bare three-argument call binds to has already changed
        // between StackExchange.Redis versions. A matcher written against the wrong one matches
        // nothing and says so; a matcher written against the right one starts lying the day the
        // package is bumped.
        var call = Assert.Single(
            database.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync));
        var args = call.GetArguments();

        Assert.Equal($"translationRoom:{RoomId}:transcript_paused", ((RedisKey)args[0]!).ToString());
        Assert.Contains("\"paused\":true", ((RedisValue)args[1]!).ToString());

        // The TTL is the backstop that stops a flag orphaned by a lost Resume from outliving the
        // meeting forever, so a set with no expiry at all is a real regression, not a detail.
        // The library renders the expiry either as the TimeSpan handed in or as its own Expiration
        // ("EX 43200"), depending on which overload the call binds to — hence both spellings.
        Assert.Contains(
            args,
            a => a is TimeSpan ttl
                ? ttl == TranscriptPauseKey.Ttl
                : a?.ToString() == $"EX {(long)TranscriptPauseKey.Ttl.TotalSeconds}");
    }

    /// <summary>
    /// Resume DELETES the key rather than writing a "false". Every reader tests existence, so a
    /// value nobody parses cannot drift out of agreement with the one that matters.
    /// </summary>
    [Fact]
    public async Task Resume_DeletesTheDurableFlag()
    {
        var host = Guid.NewGuid();
        var (service, _, database) = CreateService(host, activeWindow: new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            PausedBy = host,
        });

        Assert.True((await service.ResumeAsync(RoomId, host)).IsSuccess);

        await database.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => (string)k! == TranscriptPauseKey.For(RoomId)),
            Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// A Redis that will not take the flag must not fail a Pause the host has already been told
    /// succeeded — the window is committed, and the database is what the saved record and the
    /// divider are built from. Same posture the publish beside it has always had.
    /// </summary>
    [Fact]
    public async Task Pause_Succeeds_EvenWhenTheFlagCannotBeWritten()
    {
        var host = Guid.NewGuid();
        var (service, windows, database) = CreateService(host, activeWindow: null);
        database
            .StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new StackExchange.Redis.RedisConnectionException(
                StackExchange.Redis.ConnectionFailureType.UnableToConnect, "down"));

        var result = await service.PauseAsync(RoomId, host);

        Assert.True(result.IsSuccess);
        await windows.Received(1).AddAsync(Arg.Any<TranscriptPauseWindow>(), Arg.Any<CancellationToken>());
    }

    // ── A window the meeting ended in the middle of ───────────

    /// <summary>
    /// A still-open window on a room that has finished is READ as ending when the room did.
    /// </summary>
    /// <remarks>
    /// TranslationRoomEndedConsumer closes these at the moment the room ends and is the real fix.
    /// This is the second net, and it is the only one that reaches two cases: rows already in the
    /// table from before that consumer existed, and a RoomEnded publish that was lost (pub/sub has
    /// no backlog, so a consumer that was down for that second never hears about it).
    ///
    /// A projection, not a repair: the stored row keeps its null. Writing here would put an
    /// unlocked update inside a GET any participant can call, for a fact this service does not own.
    /// </remarks>
    [Fact]
    public async Task AnOpenWindowOnAFinishedRoom_IsShownEndingWhenTheRoomDid()
    {
        var host = Guid.NewGuid();
        var roomEndedAt = new DateTime(2026, 9, 9, 22, 40, 0, DateTimeKind.Utc);
        var open = new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = roomEndedAt.AddMinutes(-25),
            PausedBy = host,
        };

        var (service, _, _) = CreateService(host, activeWindow: null, allWindows: new[] { open }, roomEndedAt: roomEndedAt);

        var result = await service.GetPauseWindowsAsync(RoomId, host);

        Assert.True(result.IsSuccess);
        Assert.Equal(roomEndedAt, Assert.Single(result.Value!).EndedAt);
        // The row itself is untouched — the correction lives in the response only.
        Assert.Null(open.EndedAt);
    }

    /// <summary>
    /// A room still in progress has no end time, and the window keeps its null: the transcript
    /// really is paused right now, and the panel is right to say so.
    /// </summary>
    [Fact]
    public async Task AnOpenWindowOnALiveRoom_StaysOpen()
    {
        var host = Guid.NewGuid();
        var open = new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            PausedBy = host,
        };

        var (service, _, _) = CreateService(host, activeWindow: open, allWindows: new[] { open }, roomEndedAt: null);

        var result = await service.GetPauseWindowsAsync(RoomId, host);

        Assert.True(result.IsSuccess);
        Assert.Null(Assert.Single(result.Value!).EndedAt);
    }

    private static (TranscriptRecordingService Service, ITranscriptPauseWindowRepository Windows, IDatabase Database) CreateService(
        Guid host,
        TranscriptPauseWindow? activeWindow,
        bool redis = true,
        IReadOnlyList<TranscriptPauseWindow>? allWindows = null,
        DateTime? roomEndedAt = null)
    {
        var windows = Substitute.For<ITranscriptPauseWindowRepository>();
        windows.GetActiveWindowByRoomIdAsync(RoomId, Arg.Any<CancellationToken>()).Returns(activeWindow);
        windows.GetWindowsByRoomIdAsync(RoomId, Arg.Any<CancellationToken>())
            .Returns(allWindows ?? Array.Empty<TranscriptPauseWindow>());

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.TranscriptPauseWindows.Returns(windows);

        var pauseAccess = new TranscriptPauseAccess(new FakeRoomClient(host));
        var readAccess = Substitute.For<ITranscriptReadAccess>();
        readAccess.CanReadRoomTranscriptAsync(RoomId, host, Arg.Any<CancellationToken>()).Returns(true);

        var database = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var roomEndTime = Substitute.For<ITranslationRoomEndTime>();
        roomEndTime.GetEndedAtAsync(RoomId, Arg.Any<CancellationToken>()).Returns(roomEndedAt);

        var service = new TranscriptRecordingService(
            unitOfWork,
            pauseAccess,
            readAccess,
            NullLogger<TranscriptRecordingService>.Instance,
            redis ? multiplexer : null,
            roomEndTime);

        return (service, windows, database);
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

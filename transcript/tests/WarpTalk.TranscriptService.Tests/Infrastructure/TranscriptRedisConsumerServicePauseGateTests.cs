using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;
using Xunit;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// WT-605. The gate that stops Pause Transcript from dead-lettering.
///
/// Translation and dubbing keep running through a pause, so ProcessTranslateMessageAsync/
/// ProcessTtsMessageAsync will always see a "segment not found" for every line spoken while
/// paused — that is not a rare race, it happens on every single pause. These three private
/// helpers are what tells those two handlers "this was skipped on purpose" instead of letting
/// them retry the message into the dead-letter stream — the exact failure class the
/// `__MEETING_END__` postmortem (warptalk-ai/shared/control_markers.py) describes for a
/// different sentinel that leaked the same way.
///
/// Exercised via reflection, same style TranscriptReadAccessTests/
/// AllThreeTranscriptServicesDependOnTheOneSharedPredicate already use in this project for
/// production internals that have no public seam.
/// </summary>
public class TranscriptRedisConsumerServicePauseGateTests
{
    private static readonly Guid RoomId = Guid.NewGuid();
    private static readonly Guid SegmentId = Guid.NewGuid();

    [Fact]
    public async Task IsRoomTranscriptPausedAsync_True_WhenAnOpenWindowExists()
    {
        var service = CreateService(activeWindow: new TranscriptPauseWindow
        {
            Id = Guid.NewGuid(),
            TranslationRoomId = RoomId,
            StartedAt = DateTime.UtcNow,
            PausedBy = Guid.NewGuid(),
        });

        var paused = await InvokePrivateBoolAsync(service, "IsRoomTranscriptPausedAsync", RoomId);

        Assert.True(paused);
    }

    [Fact]
    public async Task IsRoomTranscriptPausedAsync_False_WhenNoWindowIsOpen()
    {
        var service = CreateService(activeWindow: null);

        var paused = await InvokePrivateBoolAsync(service, "IsRoomTranscriptPausedAsync", RoomId);

        Assert.False(paused);
    }

    [Fact]
    public async Task ASegmentSkippedForPause_IsRecognisedByTheTranslateAndTtsGate()
    {
        var service = CreateService(activeWindow: null, out var fakeDb);

        // Not recorded yet — a genuinely-late (not paused) segment must still be retried.
        Assert.False(await InvokePrivateBoolAsync(service, "WasSegmentSkippedForPauseAsync", RoomId, SegmentId));

        // ProcessSttMessageAsync's side of the contract: mark it skipped.
        await InvokePrivateVoidAsync(service, "MarkSegmentSkippedForPauseAsync", RoomId, SegmentId);

        // ProcessTranslateMessageAsync/ProcessTtsMessageAsync's side: recognise it and ack
        // instead of retrying.
        Assert.True(await InvokePrivateBoolAsync(service, "WasSegmentSkippedForPauseAsync", RoomId, SegmentId));

        // Scoped to the room: the same segmentId under a different room must not match — two
        // rooms cannot influence each other's dead-letter behaviour.
        var otherRoom = Guid.NewGuid();
        Assert.False(await InvokePrivateBoolAsync(service, "WasSegmentSkippedForPauseAsync", otherRoom, SegmentId));

        Assert.Contains($"translationRoom:{RoomId}:transcript_paused_segments", fakeDb.Keys);
    }

    private static TranscriptRedisConsumerService CreateService(TranscriptPauseWindow? activeWindow)
        => CreateService(activeWindow, out _);

    private static TranscriptRedisConsumerService CreateService(TranscriptPauseWindow? activeWindow, out FakeRedisSetStore fakeDb)
    {
        var windows = Substitute.For<ITranscriptPauseWindowRepository>();
        windows.GetActiveWindowByRoomIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Guid>() == RoomId ? activeWindow : null);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.TranscriptPauseWindows.Returns(windows);

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork);
        var serviceProvider = services.BuildServiceProvider();

        fakeDb = new FakeRedisSetStore();
        var database = fakeDb.AsSubstitute();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        return new TranscriptRedisConsumerService(
            redis,
            NullLogger<TranscriptRedisConsumerService>.Instance,
            serviceProvider);
    }

    private static Task<bool> InvokePrivateBoolAsync(object target, string methodName, params object[] args)
        => (Task<bool>)InvokePrivate(target, methodName, args);

    private static Task InvokePrivateVoidAsync(object target, string methodName, params object[] args)
        => (Task)InvokePrivate(target, methodName, args);

    private static object InvokePrivate(object target, string methodName, object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(target.GetType().Name, methodName);
        var fullArgs = new object[args.Length + 1];
        Array.Copy(args, fullArgs, args.Length);
        fullArgs[args.Length] = CancellationToken.None;
        return method.Invoke(target, fullArgs)!;
    }

    /// <summary>A minimal in-memory stand-in for the one Redis set this gate touches — real
    /// SADD/SISMEMBER/EXPIRE semantics, scoped by key, without pulling in a full Redis mock of
    /// StackExchange.Redis.IDatabase's dozens of unrelated members.</summary>
    private sealed class FakeRedisSetStore
    {
        private readonly Dictionary<string, HashSet<string>> _sets = new();

        public IEnumerable<string> Keys => _sets.Keys;

        public IDatabase AsSubstitute()
        {
            var db = Substitute.For<IDatabase>();

            db.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(ci =>
            {
                var key = (string)ci.Arg<RedisKey>()!;
                var value = (string)ci.Arg<RedisValue>()!;
                if (!_sets.TryGetValue(key, out var set))
                    _sets[key] = set = new HashSet<string>();
                return Task.FromResult(set.Add(value));
            });

            db.SetContainsAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(ci =>
            {
                var key = (string)ci.Arg<RedisKey>()!;
                var value = (string)ci.Arg<RedisValue>()!;
                return Task.FromResult(_sets.TryGetValue(key, out var set) && set.Contains(value));
            });

            db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>()).Returns(Task.FromResult(true));

            return db;
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Shared;
using WarpTalk.Shared.Coordination;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Application.Interfaces;
using WarpTalk.TranslationRoomService.Infrastructure.Redis;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// Multi-replica: pub/sub hands every message to every replica, and a consumer group would split
/// one room's system events across replicas. These consumers do read-modify-writes whose order
/// matters, so only the elected replica may act on them.
/// </summary>
public sealed class SingleReplicaConsumerTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ParticipantOffline_IsAppliedOnlyByTheLeader(bool isLeader, int expectedCalls)
    {
        var handlers = new Dictionary<string, Action<RedisChannel, RedisValue>>();
        var subscriber = new Mock<ISubscriber>();
        subscriber
            .Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((c, h, _) => handlers[c.ToString()] = h)
            .Returns(Task.CompletedTask);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(subscriber.Object);

        var participants = new Mock<ITranslationRoomParticipantService>();
        participants
            .Setup(p => p.MarkParticipantDisconnectedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var services = new ServiceCollection().AddScoped(_ => participants.Object).BuildServiceProvider();

        var leadership = new Mock<IPubSubLeadership>();
        leadership.SetupGet(l => l.ShouldHandle).Returns(isLeader);

        var worker = new ParticipantOfflineConsumerWorker(
            redis.Object, services, NullLogger<ParticipantOfflineConsumerWorker>.Instance, leadership.Object);
        await worker.StartAsync(CancellationToken.None);
        await WaitForAsync(() => handlers.Count == 2);

        // Both channels are reported, or this replica can never stand for election.
        leadership.Verify(l => l.MarkSubscribed(ParticipantOfflineConsumerWorker.ParticipantOfflineChannel), Times.Once);
        leadership.Verify(l => l.MarkSubscribed(ParticipantOfflineConsumerWorker.ParticipantOnlineChannel), Times.Once);

        handlers[ParticipantOfflineConsumerWorker.ParticipantOfflineChannel](
            RedisChannel.Literal(ParticipantOfflineConsumerWorker.ParticipantOfflineChannel),
            $"{Guid.NewGuid()}:{Guid.NewGuid()}");
        await Task.Delay(100);

        participants.Verify(
            p => p.MarkParticipantDisconnectedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Exactly(expectedCalls));
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RoomSystemEvents_AreNotReadByAFollower_AndAreReclaimedFirstByTheLeader()
    {
        var repository = new Mock<IRedisStreamRepository>();
        repository.Setup(r => r.EnsureConsumerGroupExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.ClaimStaleAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.ReadGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        var isLeader = false;
        var leadership = new Mock<ILeaderElection>();
        leadership.SetupGet(l => l.IsLeader).Returns(() => isLeader);

        var service = new TranslationRoomEventConsumerService(
            repository.Object,
            Mock.Of<IServiceScopeFactory>(),
            NullLogger<TranslationRoomEventConsumerService>.Instance,
            leadership.Object);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(700);

        repository.Verify(
            r => r.ReadGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);

        isLeader = true;
        await WaitForAsync(() => repository.Invocations.Any(i => i.Method.Name == nameof(IRedisStreamRepository.ReadGroupAsync)));
        await service.StopAsync(CancellationToken.None);

        // The previous leader's unacknowledged entries are claimed before new ones are read.
        var calls = repository.Invocations.Select(i => i.Method.Name).ToList();
        Assert.True(
            calls.IndexOf(nameof(IRedisStreamRepository.ClaimStaleAsync))
            < calls.IndexOf(nameof(IRedisStreamRepository.ReadGroupAsync)));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

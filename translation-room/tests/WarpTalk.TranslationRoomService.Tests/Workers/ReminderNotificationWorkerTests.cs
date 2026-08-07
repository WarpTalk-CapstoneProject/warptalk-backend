using System.Linq.Expressions;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using WarpTalk.TranslationRoomService.API.Workers;
using WarpTalk.TranslationRoomService.Domain.Configuration;
using WarpTalk.TranslationRoomService.Domain.Entities;
using WarpTalk.TranslationRoomService.Domain.Interfaces;
using NotificationClient = WarpTalk.Shared.Protos.NotificationGrpcService.NotificationGrpcServiceClient;
using NotificationRequest = WarpTalk.Shared.Protos.SendNotificationRequest;
using NotificationResponse = WarpTalk.Shared.Protos.SendNotificationResponse;

namespace WarpTalk.TranslationRoomService.Tests.Workers;

/// <summary>
/// WT-326. Behavioural cover for ReminderNotificationWorker's poll, which previously had none:
/// the only existing test asserted on the file's source text, which cannot see either defect
/// this fixes.
///
///   A1 — a room whose host opened the lobby early (SCHEDULED -> WAITING, no time gate in
///        TranslationRoomService.OpenWaitingRoomAsync) was dropped from the sweep forever.
///   A2 — one failing recipient left reminder_Nmin_sent_at null, so the next poll re-sent to
///        every recipient, once a minute, for the rest of the window.
///
/// The sweep is driven directly through CheckAndSendRemindersAsync (internal, see
/// InternalsVisibleTo in the API project) so the repository predicate is exercised for real
/// rather than restated.
/// </summary>
public sealed class ReminderNotificationWorkerTests
{
    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GuestA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GuestB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ─────────────────────────────────────────────────────────────
    // A1 — opening the lobby early must not disarm the reminder
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_RemindsWaitingRoom_InsideTheTenMinuteWindow()
    {
        // The exact WT-326 shape: the host opened the lobby, so the room is WAITING, and the
        // T-10min window is open. Before the fix this room was not even returned by the query.
        var room = Room(status: "WAITING", startsIn: TimeSpan.FromMinutes(5));
        var harness = new Harness(room);

        await harness.PollAsync();

        harness.Notifications.SentTo.Should().BeEquivalentTo(new[] { HostId.ToString() });
        room.Reminder10MinSentAt.Should().NotBeNull("a WAITING room inside the window is still owed its reminder");
    }

    [Fact]
    public async Task Sweep_RemindsWaitingRoom_InsideTheOneMinuteWindow()
    {
        var room = Room(status: "WAITING", startsIn: TimeSpan.FromSeconds(30));
        room.Reminder10MinSentAt = DateTime.UtcNow.AddMinutes(-5);
        var harness = new Harness(room);

        await harness.PollAsync();

        harness.Notifications.SentTo.Should().HaveCount(1);
        room.Reminder1MinSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_StillRemindsScheduledRoom()
    {
        // Regression guard: widening the status filter must not narrow it.
        var room = Room(status: "SCHEDULED", startsIn: TimeSpan.FromMinutes(5));
        var harness = new Harness(room);

        await harness.PollAsync();

        room.Reminder10MinSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_NeverRemindsAfterTheMeetingHasStarted()
    {
        // The time gate is ReminderWindowEvaluator's, not the status filter's — which is exactly
        // why widening the status filter is safe. A WAITING room whose start time has passed must
        // stay silent.
        var room = Room(status: "WAITING", startsIn: TimeSpan.FromMinutes(-5));
        var harness = new Harness(room);

        await harness.PollAsync();

        harness.Notifications.Attempts.Should().BeEmpty();
        room.Reminder10MinSentAt.Should().BeNull();
        room.Reminder1MinSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_IgnoresRoomsFurtherOutThanTheWidestWindow()
    {
        var room = Room(status: "SCHEDULED", startsIn: TimeSpan.FromHours(3));
        var harness = new Harness(room);

        await harness.PollAsync();

        harness.Notifications.Attempts.Should().BeEmpty();
        room.Reminder10MinSentAt.Should().BeNull();
    }

    [Theory]
    [InlineData("IN_PROGRESS")]
    [InlineData("CANCELLED")]
    [InlineData("ENDED")]
    [InlineData("EXPIRED")]
    public async Task Sweep_IgnoresRoomsThatAreNoLongerPending(string status)
    {
        var room = Room(status: status, startsIn: TimeSpan.FromMinutes(5));
        var harness = new Harness(room);

        await harness.PollAsync();

        harness.Notifications.Attempts.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // A2 — one failing recipient must not re-notify the room
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneFailingRecipient_DoesNotResendToTheOthersOnTheNextPoll()
    {
        var room = Room(status: "SCHEDULED", startsIn: TimeSpan.FromMinutes(5), participants: new[] { GuestA, GuestB });
        var harness = new Harness(room);
        harness.Notifications.FailFor.Add(GuestB.ToString());

        await harness.PollAsync();

        harness.Notifications.Attempts.Should().HaveCount(3, "every recipient is tried on the first pass");
        harness.Notifications.SentTo.Should().BeEquivalentTo(new[] { HostId.ToString(), GuestA.ToString() });
        room.Reminder10MinSentAt.Should().BeNull("the room is not fully notified while one recipient is still owed the reminder");

        harness.Notifications.Reset();
        await harness.PollAsync();

        // THE DEFECT: before the fix this was 3 again — and 3 again on every poll after that.
        harness.Notifications.Attempts.Should().BeEquivalentTo(new[] { GuestB.ToString() },
            "only the recipient who did not get it is retried");
        harness.Notifications.SentTo.Should().BeEmpty("the retry failed again, but nobody else was disturbed");
    }

    [Fact]
    public async Task FailingRecipientRecovers_ThenTheRoomIsStampedAndEveryoneStopsBeingNotified()
    {
        var room = Room(status: "SCHEDULED", startsIn: TimeSpan.FromMinutes(5), participants: new[] { GuestA, GuestB });
        var harness = new Harness(room);
        harness.Notifications.FailFor.Add(GuestB.ToString());

        await harness.PollAsync();
        room.Reminder10MinSentAt.Should().BeNull();

        harness.Notifications.FailFor.Clear();
        harness.Notifications.Reset();
        await harness.PollAsync();

        harness.Notifications.SentTo.Should().BeEquivalentTo(new[] { GuestB.ToString() },
            "the recipient who was missed gets exactly one reminder, and the other two get no second one");
        room.Reminder10MinSentAt.Should().NotBeNull("everybody has now been reminded");

        harness.Notifications.Reset();
        await harness.PollAsync();
        harness.Notifications.Attempts.Should().BeEmpty("the stamped column takes the room out of the window entirely");
    }

    [Fact]
    public async Task EveryRecipientSucceeds_StampsOnceAndNeverRepeats()
    {
        var room = Room(status: "WAITING", startsIn: TimeSpan.FromMinutes(5), participants: new[] { GuestA, GuestB });
        var harness = new Harness(room);

        await harness.PollAsync();
        harness.Notifications.SentTo.Should().HaveCount(3);
        var stampedAt = room.Reminder10MinSentAt;
        stampedAt.Should().NotBeNull();

        harness.Notifications.Reset();
        await harness.PollAsync();

        harness.Notifications.Attempts.Should().BeEmpty();
        room.Reminder10MinSentAt.Should().Be(stampedAt);
    }

    [Fact]
    public async Task RecipientMarkersAreScopedPerWindow()
    {
        // A recipient reminded at T-10min must still be reminded at T-1min: the per-recipient
        // marker keys carry the window, so they cannot suppress the other one.
        var room = Room(status: "WAITING", startsIn: TimeSpan.FromMinutes(5));
        var harness = new Harness(room);

        await harness.PollAsync();
        harness.Notifications.SentTo.Should().HaveCount(1);

        room.ScheduledAt = DateTime.UtcNow.AddSeconds(30);
        harness.Notifications.Reset();
        await harness.PollAsync();

        harness.Notifications.SentTo.Should().BeEquivalentTo(new[] { HostId.ToString() });
        room.Reminder1MinSentAt.Should().NotBeNull();
    }

    // ─────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────

    private static TranslationRoom Room(string status, TimeSpan startsIn, Guid[]? participants = null)
    {
        var room = new TranslationRoom
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            HostId = HostId,
            Title = "Sprint review",
            TranslationRoomCode = "ABC-123",
            Status = status,
            TranslationRoomType = "INSTANT",
            SourceLanguage = "en",
            TargetLanguages = "[\"vi\"]",
            Settings = "{}",
            ScheduledAt = DateTime.UtcNow.Add(startsIn),
        };

        foreach (var userId in participants ?? Array.Empty<Guid>())
        {
            room.TranslationRoomParticipants.Add(new TranslationRoomParticipant
            {
                Id = Guid.NewGuid(),
                TranslationRoomId = room.Id,
                UserId = userId,
                DisplayName = "Participant",
                Role = "PARTICIPANT",
                ListenLanguage = "vi",
                SpeakLanguage = "vi",
                Status = "WAITING",
                ConnectionType = "WEB",
            });
        }

        return room;
    }

    private sealed class Harness
    {
        private readonly ReminderNotificationWorker _worker;

        public RecordingNotificationClient Notifications { get; } = new();

        public Harness(params TranslationRoom[] rooms)
        {
            var roomRepository = new Mock<ITranslationRoomRepository>();
            roomRepository
                .Setup(r => r.FindAsync(
                    It.IsAny<Expression<Func<TranslationRoom, bool>>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Expression<Func<TranslationRoom, bool>> predicate, string _, CancellationToken _) =>
                    (IReadOnlyList<TranslationRoom>)rooms.Where(predicate.Compile()).ToList());

            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.TranslationRoomRepository).Returns(roomRepository.Object);
            unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            var services = new ServiceCollection();
            services.AddScoped(_ => unitOfWork.Object);

            _worker = new ReminderNotificationWorker(
                services.BuildServiceProvider(),
                NullLogger<ReminderNotificationWorker>.Instance,
                Options.Create(new AppSettings { FrontendBaseUrl = "https://warptalk.test" }),
                Notifications,
                new FakeRedis().Multiplexer);
        }

        public Task PollAsync() => _worker.CheckAndSendRemindersAsync(CancellationToken.None);
    }

    /// <summary>
    /// The generated gRPC client exists to be subclassed for exactly this (see its protected
    /// parameterless constructor). Overriding the async overload the worker calls lets a test
    /// fail one specific recipient, which is the whole point of A2.
    /// </summary>
    private sealed class RecordingNotificationClient : NotificationClient
    {
        public List<string> Attempts { get; } = new();
        public List<string> SentTo { get; } = new();
        public HashSet<string> FailFor { get; } = new();

        public void Reset()
        {
            Attempts.Clear();
            SentTo.Clear();
        }

        public override AsyncUnaryCall<NotificationResponse> SendNotificationAsync(
            NotificationRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(request.UserId);
            if (FailFor.Contains(request.UserId))
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "NotificationService is unreachable"));
            }

            SentTo.Add(request.UserId);
            return new AsyncUnaryCall<NotificationResponse>(
                Task.FromResult(new NotificationResponse { Success = true, NotificationId = Guid.NewGuid().ToString() }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }

    /// <summary>
    /// A Redis double with real key semantics: the per-recipient markers are the mechanism under
    /// test in A2, so a mock that returns a canned "false" for KeyExistsAsync would prove nothing.
    /// </summary>
    private sealed class FakeRedis
    {
        private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);

        public IConnectionMultiplexer Multiplexer { get; }

        public FakeRedis()
        {
            var database = new Mock<IDatabase>();

            database
                .Setup(d => d.LockTakeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            database
                .Setup(d => d.LockReleaseAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            database
                .Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, CommandFlags _) => _keys.ContainsKey(key.ToString()!));
            // StackExchange.Redis 3.x resolves StringSetAsync(key, value, TimeSpan) to the
            // Expiration/ValueCondition overload — not the older keepTtl one. Stubbing the wrong
            // overload leaves the marker unwritten, which shows up here as a failing A2 test
            // rather than as a silent pass.
            database
                .Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(),
                    It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey key, RedisValue value, Expiration _, ValueCondition _, CommandFlags _) =>
                {
                    _keys[key.ToString()!] = value.ToString();
                    return true;
                });

            var multiplexer = new Mock<IConnectionMultiplexer>();
            multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
            Multiplexer = multiplexer.Object;
        }
    }
}

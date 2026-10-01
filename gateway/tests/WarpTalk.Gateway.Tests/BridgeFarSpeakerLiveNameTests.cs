using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WarpTalk.Gateway.Hubs;
using WarpTalk.Gateway.Services;
using WarpTalk.Shared;
using WarpTalk.Shared.Protos;
using TranscriptSegmentDto = WarpTalk.Gateway.Hubs.TranscriptSegmentDto;

namespace WarpTalk.Gateway.Tests;

/// <summary>
/// The live name of a line spoken on the Google Meet side of a bridge room. The stand-in's LiveKit
/// name is the seat ("External Meeting"), not a person, so the line is named from stt_worker's
/// far-side hint instead — the same <see cref="FarSpeakerNames"/> rule TranscriptService applies to
/// the saved row, so a reload does not rename the speaker.
/// </summary>
public sealed class BridgeFarSpeakerLiveNameTests
{
    private const string RoomId = "8f14e45f-ce0a-4a4f-9f5b-2c3d4e5f6a7b";
    private static readonly string StandIn = ExternalBridgeConstants.ParticipantUserId.ToString();

    private static StreamEntry Entry(params (string Name, string Value)[] fields) =>
        new("1-0", fields.Select(f => new NameValueEntry(f.Name, f.Value)).ToArray());

    [Theory]
    [InlineData("Alice Nguyen", "0.6", "Alice Nguyen")]
    [InlineData("Alice Nguyen", "0.95", "Alice Nguyen")]
    [InlineData("Alice Nguyen", "0.59", "Google Meet participants")]
    [InlineData("Alice Nguyen", "", "Google Meet participants")]
    [InlineData("", "0.95", "Google Meet participants")]
    public void StandIn_IsNamedFromTheHint_AtOrAboveTheThreshold(string name, string confidence, string expected)
    {
        var entry = Entry(
            ("speaker_id", StandIn),
            ("far_speaker_name", name),
            ("far_speaker_confidence", confidence));

        Assert.Equal(expected, AiResultConsumerService.TryResolveStandInSpeakerName(entry, FarSpeakerNames.DefaultMinConfidence));
    }

    [Fact]
    public void StandIn_WithNoHintFieldsAtAll_IsGoogleMeetParticipants()
    {
        Assert.Equal(
            "Google Meet participants",
            AiResultConsumerService.TryResolveStandInSpeakerName(Entry(("speaker_id", StandIn)), 0.6));
    }

    [Fact]
    public void ARealParticipant_IsNotTheStandInRule()
    {
        var entry = Entry(
            ("speaker_id", Guid.NewGuid().ToString()),
            ("far_speaker_name", "Mallory"),
            ("far_speaker_confidence", "1"));

        Assert.Null(AiResultConsumerService.TryResolveStandInSpeakerName(entry, 0.6));
    }

    [Fact]
    public async Task Broadcast_CarriesTheLiveName_NotTheSeatName()
    {
        await using var harness = new Harness(
            config: null,
            ("speaker_id", StandIn),
            ("far_speaker_name", "Alice Nguyen"),
            ("far_speaker_source", "meet_caption"),
            ("far_speaker_confidence", "0.8"));

        var segment = await harness.FirstSegmentAsync();

        Assert.Equal("Alice Nguyen", segment.SpeakerName);
        Assert.Equal(ExternalBridgeConstants.ParticipantUserId, segment.SpeakerId);
    }

    [Fact]
    public async Task Broadcast_BelowTheThreshold_CarriesGoogleMeetParticipants()
    {
        await using var harness = new Harness(
            config: null,
            ("speaker_id", StandIn),
            ("far_speaker_name", "Alice Nguyen"),
            ("far_speaker_confidence", "0.3"));

        Assert.Equal("Google Meet participants", (await harness.FirstSegmentAsync()).SpeakerName);
    }

    [Fact]
    public async Task Broadcast_UsesTheConfiguredThreshold()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [FarSpeakerNames.MinConfidenceConfigKey] = "0.25" })
            .Build();
        await using var harness = new Harness(
            config,
            ("speaker_id", StandIn),
            ("far_speaker_name", "Alice Nguyen"),
            ("far_speaker_confidence", "0.3"));

        Assert.Equal("Alice Nguyen", (await harness.FirstSegmentAsync()).SpeakerName);
    }

    [Fact]
    public async Task Broadcast_ARealParticipant_KeepsTheirOwnName()
    {
        var person = Guid.NewGuid().ToString();
        await using var harness = new Harness(
            config: null,
            ("speaker_id", person),
            ("far_speaker_name", "Mallory"),
            ("far_speaker_confidence", "1"));

        Assert.Equal("Name of " + person, (await harness.FirstSegmentAsync()).SpeakerName);
    }

    /// <summary>
    /// The real AiResultConsumerService over a mocked Redis with one stt:results entry. The
    /// speaker-name hash answers "External Meeting" for the stand-in — what the ingress worker
    /// writes from its LiveKit token — so a test passes only if that is NOT what is broadcast.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AiResultConsumerService _service;
        private readonly Mock<IDatabase> _database = new();
        private readonly Mock<IClientProxy> _proxy = new();
        private readonly (string Name, string Value)[] _fields;
        private int _reads;

        public Harness(IConfiguration? config, params (string Name, string Value)[] speakerFields)
        {
            _fields = speakerFields;
            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);

            _database
                .Setup(d => d.StreamCreateConsumerGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue?>(),
                    It.IsAny<bool>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _database
                .Setup(d => d.StreamReadGroupAsync(
                    It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<RedisValue?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue _, RedisValue __, RedisValue? ___, int? ____,
                          bool _____, TimeSpan? ______, CommandFlags _______) =>
                    Task.FromResult(NextBatch(key.ToString())));
            _database
                .Setup(d => d.HashGetAsync(
                    It.Is<RedisKey>(k => k.ToString() == $"meeting:{RoomId}:speaker_names"),
                    It.IsAny<RedisValue>(),
                    It.IsAny<CommandFlags>()))
                .Returns((RedisKey _, RedisValue field, CommandFlags __) => Task.FromResult(
                    field.ToString() == StandIn ? (RedisValue)"External Meeting" : (RedisValue)("Name of " + field)));

            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
            var hubContext = new Mock<IHubContext<TranslationRoomHub>>();
            hubContext.Setup(c => c.Clients).Returns(clients.Object);

            _service = new AiResultConsumerService(
                new RedisStreamService(redis.Object, NullLogger<RedisStreamService>.Instance, new ConfigurationBuilder().Build()),
                new ActiveTranslationRoomRegistry(),
                hubContext.Object,
                new FakeWorkspaceClient(),
                new FakeRoomClient(),
                NullLogger<AiResultConsumerService>.Instance,
                config);
        }

        private StreamEntry[] NextBatch(string streamKey)
        {
            if (streamKey != "stt:results" || Interlocked.Increment(ref _reads) > 1)
                return Array.Empty<StreamEntry>();

            var fields = new List<(string Name, string Value)>
            {
                ("meeting_id", RoomId),
                ("segment_id", Guid.NewGuid().ToString()),
                ("text", "Can everybody hear me now?"),
                ("language", "en"),
                ("start_ms", "0"),
                ("end_ms", "1200"),
            };
            fields.AddRange(_fields);
            return new[] { Entry(fields.ToArray()) };
        }

        public async Task<TranscriptSegmentDto> FirstSegmentAsync()
        {
            await _service.StartAsync(CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var sent = _proxy.Invocations.FirstOrDefault(i =>
                    i.Method.Name == nameof(IClientProxy.SendCoreAsync)
                    && (string)i.Arguments[0] == "TranscriptSegmentReceived");
                if (sent is not null)
                    return Assert.IsType<TranscriptSegmentDto>(((object?[])sent.Arguments[1])[0]);
                await Task.Delay(10);
            }

            throw new TimeoutException("No TranscriptSegmentReceived was broadcast.");
        }

        public async ValueTask DisposeAsync() => await _service.StopAsync(CancellationToken.None);
    }

    private sealed class FakeRoomClient : TranslationRoomService.TranslationRoomServiceClient
    {
        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            Call(new GetTranslationRoomResponse
            {
                Id = request.Id,
                WorkspaceId = Guid.NewGuid().ToString(),
                HostId = Guid.NewGuid().ToString(),
                Status = "IN_PROGRESS",
            });

        internal static AsyncUnaryCall<T> Call<T>(T value) => new(
            Task.FromResult(value),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private sealed class FakeWorkspaceClient : WorkspaceService.WorkspaceServiceClient
    {
        public override AsyncUnaryCall<GetWorkspaceSettingsResponse> GetWorkspaceSettingsAsync(
            GetWorkspaceSettingsRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            FakeRoomClient.Call(new GetWorkspaceSettingsResponse
            {
                IsProfanityFilterEnabled = false,
                AllowExternalLlm = false,
            });
    }
}

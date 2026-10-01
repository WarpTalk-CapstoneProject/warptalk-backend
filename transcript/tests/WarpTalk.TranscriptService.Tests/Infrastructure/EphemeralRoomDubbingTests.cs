using System.Linq.Expressions;
using System.Reflection;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using WarpTalk.Shared.Protos;
using WarpTalk.TranscriptService.Domain.Entities;
using WarpTalk.TranscriptService.Domain.Interfaces;
using WarpTalk.TranscriptService.Infrastructure.Redis;

namespace WarpTalk.TranscriptService.Tests.Infrastructure;

/// <summary>
/// WT-587 on the tts:results path. An ephemeral meeting (save_transcript=false) is still dubbed,
/// but the STT and translate handlers write no segment and no translation link for it, so the
/// dub's link lookup can never succeed. Without the retention gate the handler said "retry", and
/// after five attempts every dubbed line of the meeting landed in
/// <c>tts:results:transcript-persistence:dead-letter</c> — prod, 28 Sep, room "Test no save
/// transcript": the 7 entries keeping WarpTalkDeadLetterPresent firing.
/// </summary>
public class EphemeralRoomDubbingTests
{
    [Fact]
    public async Task ADubForAnEphemeralRoom_IsAcknowledged_NotRetriedIntoTheDeadLetterStream()
    {
        var handled = await ProcessTtsAsync(saveTranscript: false);

        Assert.True(handled);
    }

    [Fact]
    public async Task ADubWhoseLinkIsMissingInAKeptRoom_IsStillRetried()
    {
        // The real race this branch exists for: TTS outran translation persistence.
        var handled = await ProcessTtsAsync(saveTranscript: true);

        Assert.False(handled);
    }

    private static async Task<bool> ProcessTtsAsync(bool saveTranscript)
    {
        var links = Substitute.For<ISegmentTranslationLinkRepository>();
        links.FindAsync(Arg.Any<Expression<Func<SegmentTranslationLink, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<SegmentTranslationLink>());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SegmentTranslationLinks.Returns(links);

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork);
        services.AddScoped<TranslationRoomService.TranslationRoomServiceClient>(_ => new FakeRoomClient(saveTranscript));
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var service = new TranscriptRedisConsumerService(
            redis,
            NullLogger<TranscriptRedisConsumerService>.Instance,
            services.BuildServiceProvider());

        // The shape of the dead-lettered prod entries (audio trimmed).
        var dub = new StreamEntry("1790565125317-0",
        [
            new NameValueEntry("segment_id", $"{Guid.NewGuid()}-vi-c0"),
            new NameValueEntry("meeting_id", Guid.NewGuid().ToString()),
            new NameValueEntry("speaker_id", Guid.NewGuid().ToString()),
            new NameValueEntry("audio_data", "UklGRiQAAABXQVZF"),
            new NameValueEntry("duration_ms", "3440"),
            new NameValueEntry("voice_type", "default"),
            new NameValueEntry("anchor_provider", "cartesia"),
            new NameValueEntry("provider_voice_id", "8e8f222d-c817-4cc5-822b-8bf76ca7e98d"),
            new NameValueEntry("fallback_reason", "voice_profile_not_ready"),
            new NameValueEntry("target_lang", "vi"),
            new NameValueEntry("is_final_chunk", "0"),
        ]);

        var method = typeof(TranscriptRedisConsumerService).GetMethod(
            "ProcessTtsMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<bool>)method.Invoke(service, ["tts:results", dub, CancellationToken.None])!;
    }

    private sealed class FakeRoomClient(bool saveTranscript) : TranslationRoomService.TranslationRoomServiceClient
    {
        public override AsyncUnaryCall<GetTranslationRoomResponse> GetTranslationRoomByIdAsync(
            GetTranslationRoomRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
            => new(
                Task.FromResult(new GetTranslationRoomResponse
                {
                    Id = request.Id,
                    WorkspaceId = Guid.NewGuid().ToString(),
                    Status = "ENDED",
                    SaveTranscript = saveTranscript,
                }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
    }
}
